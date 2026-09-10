using System.Diagnostics;
using System.Globalization;

using AssistantQR.Cli.Configuration;
using AssistantQR.Cli.Rendering;

using AssistantQR.Application.Model;
using AssistantQR.Application.Ports;
using AssistantQR.Application.UseCases.AnswerQuestion;
using AssistantQR.Application.UseCases.Snapshots;

using AssistantQR.Domain.Access;
using AssistantQR.Domain.Answers;
using AssistantQR.Domain.Questions;
using AssistantQR.Domain.Text;

using Microsoft.Extensions.DependencyInjection;

namespace AssistantQR.Cli.Commands;

/// <summary>
/// SCENARIO A — <c>assistantqr demo llm-swap</c> : le meme jeu de questions, N modeles de
/// generation, une seule recuperation.
/// </summary>
/// <remarks>
/// CE QUE LA DEMONSTRATION MONTRE, ET CE QU'ELLE NE MONTRE PAS. Elle ne montre pas qu'un
/// modele est meilleur que l'autre — aucune machine ne peut le dire ici. Elle montre que
/// la substitution est LOCALE : les extraits remontes par l'index sont, fragment par
/// fragment et score par score, exactement les memes ; seule la redaction change. C'est la
/// consequence directe de l'etroitesse de <c>ILanguageModel</c>, et c'est ce qui permet
/// d'affirmer que les regles metier tiennent independamment du modele — elles n'ont jamais
/// ete confiees au modele.
///
/// TOUS LES MONTAGES PARTAGENT LITTERALEMENT LE MEME INDEX. Les montages suivants recoivent
/// l'instance construite par le premier ; il n'y a donc pas de reindexation entre les
/// passages, et l'egalite des recuperations n'est pas une coincidence heureuse mais une
/// propriete verifiee.
///
/// POURQUOI N MODELES ET NON DEUX. Deux modeles ne produisent qu'un seul chiffre, et un
/// seul chiffre ne se lit pas : on ignore s'il mesure l'ecart entre deux redactions de
/// qualite comparable ou une violation de contrat. Trois modeles donnent deux paires que
/// l'on peut lire l'une contre l'autre — un modele de classe voisine, un modele beaucoup
/// plus petit — et c'est la comparaison des paires, pas la paire seule, qui enseigne
/// quelque chose. Le PREMIER modele de la liste sert de reference a toutes les paires.
///
/// LA MESURE DES CHAINES DE CITATIONS DISTINGUE LE VIDE DU PLEIN. Deux refus produisent
/// deux chaines de citations vides, donc « identiques » ; les compter comme un accord
/// gonfle l'accord exactement la ou les deux modeles n'ont rien produit. Le nombre de
/// chaines identiques NON VIDES est donc affiche separement, et le nombre de chaines
/// identiques parce que vides des deux cotes est affiche a cote de lui.
/// </remarks>
internal static class LlmSwapDemo
{
    /// <summary>Options propres a la demonstration.</summary>
    public static readonly string[] OwnOptions = { "questions", "models" };

    /// <summary>
    /// Les deux faux deterministes sont disponibles dans les deux profils : la
    /// demonstration part donc d'eux, et « --models a,b,c » permet d'y substituer de
    /// vrais modeles quand Ollama est la.
    /// </summary>
    private static readonly string[] DefaultModels = { "extractive-fake", "replay" };

    /// <summary>Une comparaison exige une reference et au moins un modele a lui opposer.</summary>
    private const int MinimumModels = 2;

    /// <summary>Largeur minimale d'une colonne de modele dans le tableau de synthese.</summary>
    private const int MinimumModelColumn = 14;

    /// <summary>
    /// Largeur maximale d'une colonne de modele. Elle est calee sur la limite que
    /// <see cref="ConsoleRenderer.Table"/> impose aux colonnes de tete : au-dela, c'est le
    /// tableau qui tronque, avec « ... », et la legende annoncerait une marque de troncature
    /// que la cellule ne porte pas.
    /// </summary>
    private const int MaximumModelColumn = 40;

    /// <summary>Rejoue le jeu de questions avec N modeles de generation.</summary>
    public static async Task<int> RunAsync(
        CommandLine commandLine,
        ConsoleRenderer renderer,
        CancellationToken cancellationToken)
    {
        commandLine.EnsureKnownOptions(CliConfiguration.OverrideOptions.Concat(OwnOptions).ToArray());

        var models = commandLine.Values("models", MinimumModels) ?? DefaultModels;

        // Un jeu de reglages complet par modele : seule la ligne « LanguageModel.Model »
        // differe d'un montage a l'autre, et c'est precisement la these du scenario.
        var optionsPerModel = models
            .Select(model =>
            {
                var options = CliConfiguration.Load(commandLine);
                options.LanguageModel.Model = model;
                return options;
            })
            .ToList();

        var questions = await QuestionSet
            .LoadAsync(optionsPerModel[0], commandLine.Value("questions"), cancellationToken)
            .ConfigureAwait(false);

        renderer.Title("Scenario A — changer de modele de generation");

        var header = new List<(string Label, string Value)>(models.Count + 2)
        {
            ("Modele 1 (reference)", models[0]),
        };

        for (var i = 1; i < models.Count; i++)
        {
            header.Add(($"Modele {(i + 1).ToString(CultureInfo.InvariantCulture)}", models[i]));
        }

        header.Add(("Questions", questions.Count.ToString(CultureInfo.InvariantCulture)));
        header.Add(("Profil", optionsPerModel[0].Profile));
        renderer.Pairs(header);

        renderer.Line();

        var hosts = new List<CliHost>(models.Count);

        try
        {
            var referenceHost = CliHost.Create(optionsPerModel[0]);
            hosts.Add(referenceHost);

            await CliHost.EnsureIndexAsync(referenceHost, renderer, cancellationToken).ConfigureAwait(false);

            // Les montages suivants recoivent l'index et le service d'embeddings du
            // premier : c'est ce partage qui donne son sens a la phrase « sans reindexer ».
            var sharedIndex = referenceHost.Service<IVectorIndex>();
            var sharedEmbeddings = referenceHost.Service<IEmbeddingService>();

            for (var i = 1; i < models.Count; i++)
            {
                hosts.Add(CliHost.Create(optionsPerModel[i], services =>
                {
                    services.AddSingleton<IVectorIndex>(sharedIndex);
                    services.AddSingleton<IEmbeddingService>(sharedEmbeddings);
                }));
            }

            var metadata = await sharedIndex.GetMetadataAsync(cancellationToken).ConfigureAwait(false);
            renderer.Note(
                $"Index partage par les {models.Count.ToString(CultureInfo.InvariantCulture)} montages : " +
                $"{metadata.ChunkCount.ToString(CultureInfo.InvariantCulture)} morceau(x), " +
                $"modele d'embeddings « {metadata.EmbeddingModel} », " +
                $"decoupage « {metadata.ChunkingStrategyId} ». " +
                "Il n'est reconstruit a aucun moment : les modeles de langue se succedent sur le meme index.");

            var useCases = hosts.Select(host => host.Service<AnswerQuestionUseCase>()).ToList();
            var runs = await ExecuteAsync(questions, useCases, cancellationToken).ConfigureAwait(false);

            var comparisons = new List<Comparison>(models.Count - 1);
            for (var i = 1; i < models.Count; i++)
            {
                comparisons.Add(new Comparison(i, models[i], runs));
            }

            RenderRetrieval(renderer, runs, models.Count);
            RenderComparisons(renderer, comparisons, models[0], runs.Count);
            RenderDivergences(renderer, runs, models);
            RenderTimings(renderer, runs, models);
            RenderSummaryTable(renderer, runs, models);
            RenderConclusion(renderer, comparisons, models, runs.Count);

            return ExitCodes.Success;
        }
        finally
        {
            foreach (var host in hosts)
            {
                host.Dispose();
            }
        }
    }

    /// <summary>
    /// Joue chaque question sur chaque modele, dans l'ordre de la liste, en chronometrant
    /// chaque appel. Le temps est un resultat de la comparaison, pas un detail de
    /// deroulement : un modele qui repond aussi bien en dix fois plus de temps n'est pas
    /// substituable au meme cout.
    /// </summary>
    private static async Task<IReadOnlyList<Run>> ExecuteAsync(
        IReadOnlyList<QuestionSetItem> questions,
        IReadOnlyList<AnswerQuestionUseCase> useCases,
        CancellationToken cancellationToken)
    {
        var runs = new List<Run>(questions.Count);

        foreach (var item in questions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var command = new AnswerQuestionCommand(
                Question.From(item.Question),
                Requester.Create(item.UserId, item.Clearance));

            var results = new List<AnswerQuestionResult>(useCases.Count);
            var durations = new List<TimeSpan>(useCases.Count);

            foreach (var useCase in useCases)
            {
                var stopwatch = Stopwatch.StartNew();
                var result = await useCase.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
                stopwatch.Stop();

                results.Add(result);
                durations.Add(stopwatch.Elapsed);
            }

            runs.Add(new Run(item, results, durations));
        }

        return runs;
    }

    private static void RenderRetrieval(ConsoleRenderer renderer, IReadOnlyList<Run> runs, int modelCount)
    {
        renderer.Section("Recuperation : identique pour tous les modeles ?");

        var rows = new List<IReadOnlyList<string>>(runs.Count);

        for (var i = 0; i < runs.Count; i++)
        {
            var run = runs[i];

            var different = new List<string>();
            for (var model = 1; model < modelCount; model++)
            {
                if (!run.SameRetrievalAs(model))
                {
                    different.Add((model + 1).ToString(CultureInfo.InvariantCulture));
                }
            }

            rows.Add(new[]
            {
                (i + 1).ToString(CultureInfo.InvariantCulture),
                different.Count == 0 ? "identique" : "DIFFERENTE : " + string.Join(", ", different),
                run.Reference.Trace.CandidatesFromIndex.ToString(CultureInfo.InvariantCulture),
                run.Reference.Trace.CandidatesAfterAccessFilter.ToString(CultureInfo.InvariantCulture),
                TextExcerpt.Shorten(run.Item.Question, 60),
            });
        }

        renderer.Table(new[] { "#", "Recuperation", "Bruts", "Retenus", "Question" }, rows);
        renderer.Line();
        renderer.Note(
            "« DIFFERENTE : 3 » designe le rang du modele dans la liste --models. Les colonnes " +
            "« Bruts » et « Retenus » sont celles du modele de reference.");
    }

    private static void RenderComparisons(
        ConsoleRenderer renderer,
        IReadOnlyList<Comparison> comparisons,
        string referenceModel,
        int questionCount)
    {
        var total = questionCount.ToString(CultureInfo.InvariantCulture);

        foreach (var comparison in comparisons)
        {
            renderer.Section(
                $"Paire : « {referenceModel} » (1) contre « {comparison.Model} » " +
                $"({(comparison.Index + 1).ToString(CultureInfo.InvariantCulture)})");

            // Les etiquettes tiennent en 32 caracteres : au-dela, la colonne de gauche du
            // rendu les tronque et l'on ne sait plus lequel des quatre comptages on lit.
            renderer.Pairs(new[]
            {
                ("Recuperations identiques",
                    $"{comparison.SameRetrieval.ToString(CultureInfo.InvariantCulture)} / {total} " +
                    "(memes morceaux, memes scores, meme ordre)"),
                ("Reponses differentes",
                    $"{comparison.DifferentAnswers.ToString(CultureInfo.InvariantCulture)} / {total}"),
                ("Chaines de citations differentes",
                    $"{comparison.DifferentChains.ToString(CultureInfo.InvariantCulture)} / {total}"),
                ("Chaines identiques NON VIDES",
                    $"{comparison.IdenticalNonEmptyChains.ToString(CultureInfo.InvariantCulture)} / {total} " +
                    "(definition : memes documents cites, dans le meme ordre, chaine non vide)"),
                ("Chaines identiques mais VIDES",
                    $"{comparison.IdenticalEmptyChains.ToString(CultureInfo.InvariantCulture)} / {total} " +
                    "(definition : ni l'un ni l'autre n'a cite quoi que ce soit — une absence " +
                    "commune de sources, pas un accord sur des sources)"),
                ("Passages reponse vers refus",
                    comparison.AnsweredToRefused.ToString(CultureInfo.InvariantCulture)),
                ("Passages refus vers reponse",
                    comparison.RefusedToAnswered.ToString(CultureInfo.InvariantCulture)),
            });

            renderer.Line();
            renderer.Bullet(
                $"Motifs des refus de « {comparison.Model} » la ou « {referenceModel} » a repondu : " +
                DescribeReasons(comparison.ReasonsGainedByCompared));
            renderer.Bullet(
                $"Motifs des refus de « {referenceModel} » la ou « {comparison.Model} » a repondu : " +
                DescribeReasons(comparison.ReasonsGainedByReference));
            renderer.Bullet(
                $"Motifs de TOUS les refus de « {comparison.Model} » " +
                $"({comparison.TotalRefusals.ToString(CultureInfo.InvariantCulture)} / {total}) : " +
                DescribeReasons(comparison.AllReasonsOfCompared));
        }
    }

    private static void RenderDivergences(
        ConsoleRenderer renderer,
        IReadOnlyList<Run> runs,
        IReadOnlyList<string> models)
    {
        for (var model = 1; model < models.Count; model++)
        {
            var rank = (model + 1).ToString(CultureInfo.InvariantCulture);
            renderer.Section($"Divergences : « {models[0]} » contre « {models[model]} » ({rank})");

            var divergent = runs.Where(run => !run.SameAnswerAs(model)).ToList();

            if (divergent.Count == 0)
            {
                renderer.Note("(nulle part : les deux modeles ont produit la meme issue et le meme texte)");
                continue;
            }

            foreach (var run in divergent)
            {
                renderer.Line();
                renderer.Paragraph(run.Item.Question + $"  ({run.Item.UserId}, {run.Item.Clearance})");
                renderer.Line();
                renderer.SideBySide(
                    models[0],
                    models[model] + " (" + rank + ")",
                    new[] { (Describe(run.Reference.Outcome), Describe(run.Results[model].Outcome)) });
            }
        }
    }

    private static void RenderTimings(
        ConsoleRenderer renderer,
        IReadOnlyList<Run> runs,
        IReadOnlyList<string> models)
    {
        renderer.Section("Temps par modele");

        var rows = new List<IReadOnlyList<string>>(models.Count);

        for (var model = 0; model < models.Count; model++)
        {
            var index = model;
            var durations = runs.Select(run => run.Durations[index]).ToList();
            var totalTicks = durations.Sum(duration => duration.Ticks);
            var longestTicks = durations.Count == 0 ? 0L : durations.Max(duration => duration.Ticks);
            var average = durations.Count == 0 ? TimeSpan.Zero : TimeSpan.FromTicks(totalTicks / durations.Count);

            rows.Add(new[]
            {
                (model + 1).ToString(CultureInfo.InvariantCulture),
                models[model],
                durations.Count.ToString(CultureInfo.InvariantCulture),
                ConsoleRenderer.Duration(TimeSpan.FromTicks(totalTicks)),
                ConsoleRenderer.Duration(TimeSpan.FromTicks(longestTicks)),
                ConsoleRenderer.Duration(average),
            });
        }

        renderer.Table(new[] { "#", "Modele", "Appels", "Total", "Plus long", "Moyenne" }, rows);
        renderer.Line();
        renderer.Note(
            "Chaque appel est chronometre de bout en bout : recuperation comprise. La recuperation " +
            "etant partagee, l'ecart entre deux lignes tient a la generation. Le premier appel d'un " +
            "modele peut inclure son chargement en memoire par le serveur d'inference, et la charge " +
            "de la machine n'a pas ete isolee : ces durees decrivent CETTE execution, pas un debit.");
    }

    /// <summary>
    /// Le tableau a N colonnes : une ligne par question, une colonne par modele. C'est la
    /// seule vue ou l'on voit d'un coup quelle question fait diverger quel modele.
    /// </summary>
    private static void RenderSummaryTable(
        ConsoleRenderer renderer,
        IReadOnlyList<Run> runs,
        IReadOnlyList<string> models)
    {
        renderer.Section("Synthese : une ligne par question, une colonne par modele");

        // La console fait 100 colonnes en pratique : on repartit ce qui reste apres la
        // colonne « # » et les separateurs. Une cellule trop longue est tronquee par
        // « … », jamais abregee — une abreviation inventee se lirait comme un autre
        // identifiant.
        var available = renderer.Width - 4 - (2 * models.Count);
        var cell = Math.Clamp(available / Math.Max(1, models.Count), MinimumModelColumn, MaximumModelColumn);

        // « Shorten » ajoute « … » APRES la longueur demandee : on lui en demande une de
        // moins pour que la cellule tienne dans la colonne.
        var content = cell - 1;

        var headers = new List<string>(models.Count + 1) { "#" };
        for (var model = 0; model < models.Count; model++)
        {
            headers.Add(TextExcerpt.Shorten(
                (model + 1).ToString(CultureInfo.InvariantCulture) + " " + models[model], content));
        }

        var rows = new List<IReadOnlyList<string>>(runs.Count);

        for (var i = 0; i < runs.Count; i++)
        {
            var row = new List<string>(models.Count + 1) { (i + 1).ToString(CultureInfo.InvariantCulture) };

            for (var model = 0; model < models.Count; model++)
            {
                row.Add(TextExcerpt.Shorten(OutcomeCell(runs[i].Results[model].Outcome), content));
            }

            rows.Add(row);
        }

        renderer.Table(headers, rows);
        renderer.Line();
        renderer.Note(
            "Lecture : « [doc-a+doc-b] » = reponse citant ces documents dans cet ordre ; " +
            "« refus <Motif> » = refus, avec le motif du Domain tel quel ; « … » = identifiant tronque " +
            "faute de place, jamais abrege. Les numeros de ligne renvoient au tableau de recuperation " +
            "ci-dessus, qui porte le texte des questions.");
    }

    private static void RenderConclusion(
        ConsoleRenderer renderer,
        IReadOnlyList<Comparison> comparisons,
        IReadOnlyList<string> models,
        int questionCount)
    {
        var total = questionCount.ToString(CultureInfo.InvariantCulture);

        renderer.Section("Conclusion");

        foreach (var comparison in comparisons)
        {
            renderer.Bullet(
                $"« {models[0]} » (1) contre « {comparison.Model} » " +
                $"({(comparison.Index + 1).ToString(CultureInfo.InvariantCulture)}) : " +
                $"{comparison.SameRetrieval.ToString(CultureInfo.InvariantCulture)}/{total} recuperations " +
                $"identiques, {comparison.DifferentAnswers.ToString(CultureInfo.InvariantCulture)}/{total} " +
                $"reponses differentes, " +
                $"{comparison.DifferentChains.ToString(CultureInfo.InvariantCulture)}/{total} chaines de " +
                $"citations differentes, " +
                $"{comparison.IdenticalNonEmptyChains.ToString(CultureInfo.InvariantCulture)}/{total} chaines " +
                $"identiques non vides, " +
                $"{comparison.AnsweredToRefused.ToString(CultureInfo.InvariantCulture)} passage(s) " +
                $"reponse vers refus et " +
                $"{comparison.RefusedToAnswered.ToString(CultureInfo.InvariantCulture)} l'inverse.");
        }

        renderer.Line();

        var allRetrievalsIdentical = comparisons.All(comparison => comparison.SameRetrieval == questionCount);

        if (allRetrievalsIdentical)
        {
            renderer.Paragraph(
                $"Les {total} recuperations sont identiques pour les " +
                $"{models.Count.ToString(CultureInfo.InvariantCulture)} modeles, morceaux et scores compris. " +
                "L'index n'a pas ete reconstruit entre les passages : ce qui a change d'un modele a " +
                "l'autre se situe apres la recuperation.");
        }
        else
        {
            renderer.Paragraph(
                "Au moins une recuperation differe d'un modele a l'autre alors que l'index est partage. " +
                "Ce n'est pas un resultat attendu ici : verifie la trace de la ligne concernee avant " +
                "d'interpreter les chiffres de generation.");
        }

        renderer.Line();
        renderer.Paragraph(
            $"Ces chiffres portent sur {total} question(s), une execution, ce jeu de reglages et ce " +
            "gabarit de prompt. Ils ne mesurent ni la variance d'une execution a l'autre, ni la " +
            "qualite des reponses : conditionnellement a cet echantillon, ils disent combien de fois " +
            "les issues ont coincide, et rien de plus.");
    }

    /// <summary>Ventilation lisible des motifs de refus, ou « (aucun) ».</summary>
    private static string DescribeReasons(IReadOnlyList<(RefusalReason Reason, int Count)> reasons) =>
        reasons.Count == 0
            ? "(aucun)"
            : string.Join(", ", reasons.Select(entry =>
                $"{entry.Reason} x{entry.Count.ToString(CultureInfo.InvariantCulture)}"));

    /// <summary>La cellule du tableau de synthese : les citations, ou le motif du refus.</summary>
    private static string OutcomeCell(AnswerOutcome outcome) => outcome switch
    {
        AnswerOutcome.Answered => "[" + CitationChain(outcome) + "]",
        AnswerOutcome.Refused refused => "refus " + refused.Reason,
        _ => "issue inconnue",
    };

    /// <summary>
    /// La chaine de citations : les documents cites, dans l'ordre. Un refus n'en a pas,
    /// et cette chaine vide ne doit jamais compter comme un accord avec une autre chaine
    /// vide sans qu'on le dise.
    /// </summary>
    private static string CitationChain(AnswerOutcome outcome) => outcome switch
    {
        AnswerOutcome.Answered answered =>
            string.Join("+", answered.Answer.CitedDocumentIds.Select(id => id.Value)),
        _ => string.Empty,
    };

    private static string Describe(AnswerOutcome outcome) => outcome switch
    {
        AnswerOutcome.Answered answered =>
            "Sources : " + string.Join(", ", answered.Answer.CitedDocumentIds.Select(id => id.Value)) +
            "\n" + TextExcerpt.Shorten(answered.Answer.Text, 400),
        AnswerOutcome.Refused refused =>
            $"Refus ({refused.Reason})\n{refused.Explanation}",
        _ => "Issue inconnue",
    };

    /// <summary>Une question, jouee par tous les montages, dans l'ordre de « --models ».</summary>
    private sealed class Run
    {
        public Run(
            QuestionSetItem item,
            IReadOnlyList<AnswerQuestionResult> results,
            IReadOnlyList<TimeSpan> durations)
        {
            Item = item;
            Results = results;
            Durations = durations;
        }

        public QuestionSetItem Item { get; }

        /// <summary>Un resultat par modele ; l'indice 0 est la reference.</summary>
        public IReadOnlyList<AnswerQuestionResult> Results { get; }

        /// <summary>Une duree d'appel par modele, dans le meme ordre.</summary>
        public IReadOnlyList<TimeSpan> Durations { get; }

        public AnswerQuestionResult Reference => Results[0];

        /// <summary>Le modele de rang <paramref name="index"/> a-t-il recu exactement les memes extraits ?</summary>
        public bool SameRetrievalAs(int index) =>
            SameFragments(Reference.Trace.FromIndex, Results[index].Trace.FromIndex)
            && SameFragments(Reference.Trace.Supplied, Results[index].Trace.Supplied);

        /// <summary>A-t-il produit la meme issue ET le meme texte que la reference ?</summary>
        public bool SameAnswerAs(int index) =>
            string.Equals(
                AskCommand.SummariseOutcome(Reference.Outcome),
                AskCommand.SummariseOutcome(Results[index].Outcome),
                StringComparison.Ordinal)
            && string.Equals(TextOf(Reference.Outcome), TextOf(Results[index].Outcome), StringComparison.Ordinal);

        /// <summary>
        /// L'egalite porte sur les morceaux ET sur leurs scores : deux listes ordonnees de
        /// la meme facon mais notees differemment signaleraient une recuperation qui a
        /// bouge, meme si le sommet du classement n'a pas change.
        /// </summary>
        private static bool SameFragments(IReadOnlyList<ScoredFragment> left, IReadOnlyList<ScoredFragment> right)
        {
            if (left.Count != right.Count)
            {
                return false;
            }

            for (var i = 0; i < left.Count; i++)
            {
                if (!string.Equals(AskCommand.FragmentKey(left[i]), AskCommand.FragmentKey(right[i]),
                        StringComparison.Ordinal) ||
                    Math.Abs(left[i].Score - right[i].Score) > 1e-9)
                {
                    return false;
                }
            }

            return true;
        }

        private static string TextOf(AnswerOutcome outcome) => outcome switch
        {
            AnswerOutcome.Answered answered => answered.Answer.Text,
            AnswerOutcome.Refused refused => refused.Reason.ToString(),
            _ => string.Empty,
        };
    }

    /// <summary>
    /// Les chiffres d'une paire (reference, modele de rang <see cref="Index"/>). Tout est
    /// calcule une fois, a la construction : un affichage qui recompte est un affichage
    /// qui finit par compter autrement que la conclusion.
    /// </summary>
    private sealed class Comparison
    {
        public Comparison(int index, string model, IReadOnlyList<Run> runs)
        {
            Index = index;
            Model = model;

            var gainedByCompared = new Dictionary<RefusalReason, int>();
            var gainedByReference = new Dictionary<RefusalReason, int>();
            var allOfCompared = new Dictionary<RefusalReason, int>();

            foreach (var run in runs)
            {
                var reference = run.Reference.Outcome;
                var compared = run.Results[index].Outcome;

                if (run.SameRetrievalAs(index))
                {
                    SameRetrieval++;
                }

                if (!run.SameAnswerAs(index))
                {
                    DifferentAnswers++;
                }

                var referenceChain = CitationChain(reference);
                var comparedChain = CitationChain(compared);

                if (!string.Equals(referenceChain, comparedChain, StringComparison.Ordinal))
                {
                    DifferentChains++;
                }
                else if (referenceChain.Length > 0)
                {
                    IdenticalNonEmptyChains++;
                }
                else
                {
                    IdenticalEmptyChains++;
                }

                if (compared is AnswerOutcome.Refused refusedCompared)
                {
                    TotalRefusals++;
                    Add(allOfCompared, refusedCompared.Reason);

                    if (reference is AnswerOutcome.Answered)
                    {
                        AnsweredToRefused++;
                        Add(gainedByCompared, refusedCompared.Reason);
                    }
                }

                if (reference is AnswerOutcome.Refused refusedReference && compared is AnswerOutcome.Answered)
                {
                    RefusedToAnswered++;
                    Add(gainedByReference, refusedReference.Reason);
                }
            }

            ReasonsGainedByCompared = Sort(gainedByCompared);
            ReasonsGainedByReference = Sort(gainedByReference);
            AllReasonsOfCompared = Sort(allOfCompared);
        }

        /// <summary>Rang du modele compare dans « --models », a partir de zero.</summary>
        public int Index { get; }

        public string Model { get; }

        public int SameRetrieval { get; }

        public int DifferentAnswers { get; }

        public int DifferentChains { get; }

        public int IdenticalNonEmptyChains { get; }

        public int IdenticalEmptyChains { get; }

        public int AnsweredToRefused { get; }

        public int RefusedToAnswered { get; }

        public int TotalRefusals { get; }

        /// <summary>Motifs des refus du modele compare la ou la reference avait repondu.</summary>
        public IReadOnlyList<(RefusalReason Reason, int Count)> ReasonsGainedByCompared { get; }

        /// <summary>Motifs des refus de la reference la ou le modele compare a repondu.</summary>
        public IReadOnlyList<(RefusalReason Reason, int Count)> ReasonsGainedByReference { get; }

        /// <summary>Motifs de tous les refus du modele compare, transition ou non.</summary>
        public IReadOnlyList<(RefusalReason Reason, int Count)> AllReasonsOfCompared { get; }

        private static void Add(IDictionary<RefusalReason, int> counts, RefusalReason reason) =>
            counts[reason] = counts.TryGetValue(reason, out var current) ? current + 1 : 1;

        private static IReadOnlyList<(RefusalReason Reason, int Count)> Sort(
            IReadOnlyDictionary<RefusalReason, int> counts) =>
            counts
                .OrderByDescending(entry => entry.Value)
                .ThenBy(entry => entry.Key.ToString(), StringComparer.Ordinal)
                .Select(entry => (Reason: entry.Key, Count: entry.Value))
                .ToList();
    }
}
