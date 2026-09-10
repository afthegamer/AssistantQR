using System.Globalization;

using AssistantQR.Cli.Configuration;
using AssistantQR.Cli.Rendering;

using AssistantQR.Application.Configuration;
using AssistantQR.Application.Model;
using AssistantQR.Application.UseCases.AnswerQuestion;

using AssistantQR.Domain.Access;
using AssistantQR.Domain.Answers;
using AssistantQR.Domain.Questions;
using AssistantQR.Domain.Text;

namespace AssistantQR.Cli.Commands;

/// <summary>
/// <c>assistantqr ask</c> : poser une question, obtenir une reponse citee ou un refus motive.
/// </summary>
/// <remarks>
/// LE TRAVAIL DE CETTE COMMANDE TIENT EN TROIS GESTES : convertir deux chaines en types du
/// Domain (<see cref="Question"/> et <see cref="Requester"/>), appeler un cas d'usage, mettre
/// en forme ce qu'il rend. Elle ne decide pas si la reponse est acceptable, ne filtre aucun
/// document, ne juge aucune citation. Si l'on voulait exposer le meme service en HTTP, c'est
/// exactement ce fichier — et lui seul — qu'il faudrait reecrire.
///
/// LA CONVERSION EST FAITE ICI, ET C'EST DELIBERE. Une habilitation mal orthographiee doit
/// echouer AVANT le premier calcul de vecteur, avec une phrase qui donne les valeurs
/// acceptees. C'est le role d'une frontiere : refuser tot ce qui n'a pas de sens, pour que
/// l'interieur n'ait jamais a se demander si ses entrees sont valides.
/// </remarks>
internal static class AskCommand
{
    /// <summary>Options propres a la commande, en plus des surcharges de configuration.</summary>
    public static readonly string[] OwnOptions = { "user", "clearance", "trace" };

    /// <summary>Execute une question et affiche le resultat.</summary>
    public static async Task<int> RunAsync(
        CommandLine commandLine,
        ConsoleRenderer renderer,
        CancellationToken cancellationToken)
    {
        commandLine.EnsureKnownOptions(CliConfiguration.OverrideOptions.Concat(OwnOptions).ToArray());

        var text = commandLine.Positional(1)
                   ?? throw new UsageException(
                       "Il manque la question. Exemple : assistantqr ask \"Quels sont les horaires du samedi ?\"");

        var question = Question.From(text);
        var requester = ParseRequester(commandLine);

        var options = CliConfiguration.Load(commandLine);
        using var host = CliHost.Create(options);

        renderer.Title("Question");
        renderer.Pairs(new[]
        {
            ("Question", question.Text),
            ("Demandeur", $"{requester.Id} (habilitation « {requester.Clearance.Name} »)"),
        });

        renderer.Line();
        await CliHost.EnsureIndexAsync(host, renderer, cancellationToken).ConfigureAwait(false);

        var result = await host.Service<AnswerQuestionUseCase>()
            .ExecuteAsync(new AnswerQuestionCommand(question, requester), cancellationToken)
            .ConfigureAwait(false);

        RenderOutcome(renderer, result.Outcome);

        if (commandLine.Has("trace"))
        {
            RenderTrace(renderer, result.Trace);
        }
        else
        {
            renderer.Line();
            renderer.Note(
                $"{result.Trace.CandidatesFromIndex} candidat(s) trouves, " +
                $"{result.Trace.CandidatesAfterAccessFilter} soumis au modele, " +
                $"en {ConsoleRenderer.Duration(result.Trace.Duration)}. Ajoute --trace pour tout voir.");
        }

        if (result.Trace.IndexModelWarning is { } warning)
        {
            renderer.Section("Avertissement");
            renderer.Paragraph(warning);
        }

        return ExitCodes.Success;
    }

    /// <summary>Construit le demandeur a partir de « --user » et « --clearance ».</summary>
    /// <exception cref="UsageException">Si l'habilitation demandee n'existe pas.</exception>
    public static Requester ParseRequester(CommandLine commandLine)
    {
        var userId = commandLine.Value("user") ?? "anonymous";
        var clearance = commandLine.Value("clearance") ?? AccessLevel.Public.Name;

        if (!AccessLevel.TryParse(clearance, out var level))
        {
            throw new UsageException(
                $"Habilitation inconnue : « {clearance} ». Valeurs acceptees : " +
                string.Join(", ", AccessLevel.All.Select(item => item.Name)) + ".");
        }

        return new Requester(UserId.From(userId), level);
    }

    /// <summary>Affiche la reponse et ses citations, ou le refus et sa raison.</summary>
    public static void RenderOutcome(ConsoleRenderer renderer, AnswerOutcome outcome)
    {
        switch (outcome)
        {
            case AnswerOutcome.Answered answered:
                renderer.Section("Reponse");
                renderer.Paragraph(answered.Answer.Text);

                renderer.Section("Citations");
                renderer.Table(
                    new[] { "Document", "Niveau", "Morceau", "Extrait" },
                    answered.Answer.Citations.Select(citation => (IReadOnlyList<string>)new[]
                    {
                        citation.DocumentId.Value,
                        citation.AccessLevel.Name,
                        "#" + citation.ChunkOrdinal.ToString(CultureInfo.InvariantCulture),
                        TextExcerpt.Shorten(citation.Excerpt, 220),
                    }).ToList());
                break;

            case AnswerOutcome.Refused refused:
                renderer.Section("Refus");
                renderer.Pairs(new[]
                {
                    ("Motif", $"{refused.Reason} — {DescribeReason(refused.Reason)}"),
                    ("Explication", refused.Explanation),
                });
                break;

            default:
                renderer.Section("Resultat");
                renderer.Paragraph("Issue inattendue : la hierarchie AnswerOutcome a change sans que la CLI suive.");
                break;
        }
    }

    /// <summary>
    /// Repartit par CAUSE ce que l'index a rendu et que le modele n'a pas recu.
    /// </summary>
    /// <remarks>
    /// La trace ne porte qu'une seule liste d'ecartes — la difference entre les candidats
    /// bruts et les extraits fournis — parce que c'est tout ce que le contrat de
    /// <see cref="RetrievalTrace"/> prevoit. Or deux mecanismes independants retirent un
    /// extrait a cet endroit : le seuil de pertinence, qui ne regarde que le score, et le
    /// controle d'acces, qui ne regarde que l'habilitation. Les afficher sous une seule
    /// etiquette ferait lire « document interne cache a un demandeur interne », soit
    /// exactement l'inverse de ce que la demonstration enseigne. On les separe donc a
    /// l'affichage, a partir du seuil enregistre dans l'empreinte de configuration.
    /// </remarks>
    public static (IReadOnlyList<ScoredFragment> BelowThreshold, IReadOnlyList<ScoredFragment> Forbidden)
        SplitDropped(RetrievalTrace trace)
    {
        var belowThreshold = new List<ScoredFragment>();
        var forbidden = new List<ScoredFragment>();

        foreach (var scored in trace.DroppedByAccessFilter)
        {
            var cause = scored.Score < trace.Configuration.MinScore ? belowThreshold : forbidden;
            cause.Add(scored);
        }

        return (belowThreshold, forbidden);
    }

    /// <summary>Affiche la trace complete : bruts, ecartes, fournis, empreinte, duree.</summary>
    public static void RenderTrace(ConsoleRenderer renderer, RetrievalTrace trace)
    {
        var dropped = SplitDropped(trace);

        renderer.Section("Trace de recuperation");
        renderer.Pairs(new[]
        {
            ("Mode de filtrage", trace.FilterMode == AccessFilterMode.Pre
                ? "pre — l'index n'a jamais vu les documents interdits"
                : "post — l'index a tout classe, le filtre a retire ensuite"),
            ("Candidats bruts", trace.CandidatesFromIndex.ToString(CultureInfo.InvariantCulture)),
            ("Candidats retenus", trace.CandidatesAfterAccessFilter.ToString(CultureInfo.InvariantCulture)),
            ("Ecartes par le seuil", dropped.BelowThreshold.Count.ToString(CultureInfo.InvariantCulture)),
            ("Ecartes par l'acces", dropped.Forbidden.Count.ToString(CultureInfo.InvariantCulture)),
            ("Duree", ConsoleRenderer.Duration(trace.Duration)),
        });

        RenderFragments(renderer, "Candidats rendus par l'index", trace.FromIndex);
        RenderFragments(renderer, "Ecartes par le seuil de score", dropped.BelowThreshold);
        RenderFragments(renderer, "Ecartes par le controle d'acces", dropped.Forbidden);
        RenderFragments(renderer, "Extraits reellement soumis au modele", trace.Supplied);

        renderer.Section("Empreinte de configuration");
        renderer.Paragraph(trace.Configuration.ToString());
        renderer.Line();
        renderer.Paragraph(
            "Cette ligne est ce qu'un instantane enregistre avec les reponses. Sans elle, une derive " +
            "constatee plus tard ne s'impute a rien.");
    }

    /// <summary>Une ligne compacte decrivant l'issue, pour les tableaux de comparaison.</summary>
    public static string SummariseOutcome(AnswerOutcome outcome) => outcome switch
    {
        AnswerOutcome.Answered answered =>
            "Reponse [" + string.Join(", ", answered.Answer.CitedDocumentIds.Select(id => id.Value)) + "]",
        AnswerOutcome.Refused refused => $"Refus ({refused.Reason})",
        _ => "Issue inconnue",
    };

    /// <summary>Identifiant lisible d'un morceau : « document#ordinal ».</summary>
    public static string FragmentKey(ScoredFragment scored) =>
        $"{scored.Fragment.DocumentId}#{scored.Fragment.Ordinal.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Traduction lisible d'un motif de refus. Le cas par defaut n'est PAS silencieux :
    /// un motif ajoute au Domain et oublie ici doit se voir a l'ecran, sinon la
    /// presentation ment sur ce que la politique a decide.
    /// </summary>
    public static string DescribeReason(RefusalReason reason) => reason switch
    {
        RefusalReason.NoEvidenceInCorpus => "aucun extrait pertinent dans le corpus",
        RefusalReason.NoEvidenceReadableByRequester => "des extraits existent, aucun n'est lisible par ce demandeur",
        RefusalReason.ModelDeclinedToAnswer => "le modele a lui-meme declare que les extraits ne suffisaient pas",
        RefusalReason.ModelProducedEmptyAnswer => "le modele n'a rien produit d'exploitable",
        RefusalReason.ModelProducedNoCitation => "le modele a repondu sans citer aucune source",
        RefusalReason.ModelCitedUnknownDocument => "le modele a cite un document absent des extraits fournis",
        RefusalReason.ModelCitedForbiddenDocument => "le modele a cite un document interdit a ce demandeur",
        _ => $"ATTENTION : motif « {reason} » non traduit par la ligne de commande : "
            + "ajoutez-le a AskCommand.DescribeReason",
    };

    private static void RenderFragments(
        ConsoleRenderer renderer,
        string title,
        IReadOnlyList<ScoredFragment> fragments)
    {
        renderer.Section(title);

        if (fragments.Count == 0)
        {
            renderer.Note("(aucun)");
            return;
        }

        var rows = new List<IReadOnlyList<string>>(fragments.Count);
        for (var i = 0; i < fragments.Count; i++)
        {
            var scored = fragments[i];
            rows.Add(new[]
            {
                (i + 1).ToString(CultureInfo.InvariantCulture),
                ConsoleRenderer.Score(scored.Score),
                FragmentKey(scored),
                scored.Fragment.AccessLevel.Name,
                TextExcerpt.Shorten(scored.Fragment.Text, 200),
            });
        }

        renderer.Table(new[] { "#", "Score", "Morceau", "Niveau", "Extrait" }, rows);
    }
}
