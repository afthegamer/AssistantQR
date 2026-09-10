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
/// SCENARIO A — <c>assistantqr demo llm-swap</c> : le meme jeu de questions, deux modeles
/// de generation, une seule recuperation.
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
/// LES DEUX MONTAGES PARTAGENT LITTERALEMENT LE MEME INDEX. Le second conteneur recoit
/// l'instance construite par le premier ; il n'y a donc pas de reindexation entre les deux
/// passages, et l'egalite des recuperations n'est pas une coincidence heureuse mais une
/// propriete verifiee.
/// </remarks>
internal static class LlmSwapDemo
{
    /// <summary>Options propres a la demonstration.</summary>
    public static readonly string[] OwnOptions = { "questions", "models" };

    /// <summary>Rejoue le jeu de questions avec deux modeles de generation.</summary>
    public static async Task<int> RunAsync(
        CommandLine commandLine,
        ConsoleRenderer renderer,
        CancellationToken cancellationToken)
    {
        commandLine.EnsureKnownOptions(CliConfiguration.OverrideOptions.Concat(OwnOptions).ToArray());

        // Les deux faux deterministes sont disponibles dans les deux profils : la
        // demonstration part donc d'eux, et « --models a,b » permet d'y substituer deux
        // vrais modeles quand Ollama est la.
        var (firstModel, secondModel) = commandLine.Pair("models") ?? ("extractive-fake", "replay");

        var optionsFirst = CliConfiguration.Load(commandLine);
        optionsFirst.LanguageModel.Model = firstModel;

        var optionsSecond = CliConfiguration.Load(commandLine);
        optionsSecond.LanguageModel.Model = secondModel;

        var questions = await QuestionSet
            .LoadAsync(optionsFirst, commandLine.Value("questions"), cancellationToken)
            .ConfigureAwait(false);

        renderer.Title("Scenario A — changer de modele de generation");
        renderer.Pairs(new[]
        {
            ("Modele A", firstModel),
            ("Modele B", secondModel),
            ("Questions", questions.Count.ToString(CultureInfo.InvariantCulture)),
            ("Profil", optionsFirst.Profile),
        });

        renderer.Line();

        using var hostFirst = CliHost.Create(optionsFirst);
        await CliHost.EnsureIndexAsync(hostFirst, renderer, cancellationToken).ConfigureAwait(false);

        // Le second montage recoit l'index et le service d'embeddings du premier : c'est
        // ce partage qui donne son sens a la phrase « sans reindexer ».
        var sharedIndex = hostFirst.Service<IVectorIndex>();
        var sharedEmbeddings = hostFirst.Service<IEmbeddingService>();

        using var hostSecond = CliHost.Create(optionsSecond, services =>
        {
            services.AddSingleton<IVectorIndex>(sharedIndex);
            services.AddSingleton<IEmbeddingService>(sharedEmbeddings);
        });

        var metadata = await sharedIndex.GetMetadataAsync(cancellationToken).ConfigureAwait(false);
        renderer.Note(
            $"Index partage par les deux montages : {metadata.ChunkCount.ToString(CultureInfo.InvariantCulture)} " +
            $"morceau(x), modele « {metadata.EmbeddingModel} », decoupage « {metadata.ChunkingStrategyId} ». " +
            "Il n'est reconstruit a aucun moment.");

        var useCaseFirst = hostFirst.Service<AnswerQuestionUseCase>();
        var useCaseSecond = hostSecond.Service<AnswerQuestionUseCase>();

        var runs = new List<Run>(questions.Count);

        foreach (var item in questions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var command = new AnswerQuestionCommand(
                Question.From(item.Question),
                Requester.Create(item.UserId, item.Clearance));

            var first = await useCaseFirst.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
            var second = await useCaseSecond.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);

            runs.Add(new Run(item, first, second));
        }

        RenderRetrieval(renderer, runs);
        RenderAnswers(renderer, runs, firstModel, secondModel);

        var identical = runs.Count(run => run.SameRetrieval);
        var changed = runs.Count(run => !run.SameAnswer);

        renderer.Section("Conclusion");
        renderer.Paragraph(
            $"Conclusion : passer de « {firstModel} » a « {secondModel} » a modifie " +
            $"{changed.ToString(CultureInfo.InvariantCulture)} reponse(s) sur " +
            $"{runs.Count.ToString(CultureInfo.InvariantCulture)} et laisse " +
            $"{identical.ToString(CultureInfo.InvariantCulture)} recuperation(s) sur " +
            $"{runs.Count.ToString(CultureInfo.InvariantCulture)} strictement identiques — " +
            "le modele de langue a change, le reste du systeme ne s'en est pas apercu.");

        return ExitCodes.Success;
    }

    private static void RenderRetrieval(ConsoleRenderer renderer, IReadOnlyList<Run> runs)
    {
        renderer.Section("Recuperation : identique des deux cotes ?");

        var rows = new List<IReadOnlyList<string>>(runs.Count);

        for (var i = 0; i < runs.Count; i++)
        {
            var run = runs[i];
            rows.Add(new[]
            {
                (i + 1).ToString(CultureInfo.InvariantCulture),
                run.SameRetrieval ? "identique" : "DIFFERENTE",
                run.First.Trace.CandidatesFromIndex.ToString(CultureInfo.InvariantCulture),
                run.First.Trace.CandidatesAfterAccessFilter.ToString(CultureInfo.InvariantCulture),
                TextExcerpt.Shorten(run.Item.Question, 60),
            });
        }

        renderer.Table(new[] { "#", "Recuperation", "Bruts", "Retenus", "Question" }, rows);
    }

    private static void RenderAnswers(
        ConsoleRenderer renderer,
        IReadOnlyList<Run> runs,
        string firstModel,
        string secondModel)
    {
        renderer.Section("Issues comparees");

        var rows = new List<IReadOnlyList<string>>(runs.Count);

        for (var i = 0; i < runs.Count; i++)
        {
            var run = runs[i];
            rows.Add(new[]
            {
                (i + 1).ToString(CultureInfo.InvariantCulture),
                AskCommand.SummariseOutcome(run.First.Outcome),
                AskCommand.SummariseOutcome(run.Second.Outcome),
                run.SameAnswer ? "=" : "different",
            });
        }

        renderer.Table(new[] { "#", firstModel, secondModel, "Ecart" }, rows);

        var divergent = runs.Where(run => !run.SameAnswer).ToList();

        renderer.Section("La ou les deux modeles divergent");

        if (divergent.Count == 0)
        {
            renderer.Note("(nulle part : les deux modeles ont redige la meme chose)");
            return;
        }

        foreach (var run in divergent)
        {
            renderer.Line();
            renderer.Paragraph(run.Item.Question + $"  ({run.Item.UserId}, {run.Item.Clearance})");
            renderer.Line();
            renderer.SideBySide(
                firstModel,
                secondModel,
                new[] { (Describe(run.First.Outcome), Describe(run.Second.Outcome)) });
        }
    }

    private static string Describe(AnswerOutcome outcome) => outcome switch
    {
        AnswerOutcome.Answered answered =>
            "Sources : " + string.Join(", ", answered.Answer.CitedDocumentIds.Select(id => id.Value)) +
            "\n" + TextExcerpt.Shorten(answered.Answer.Text, 400),
        AnswerOutcome.Refused refused =>
            $"Refus ({refused.Reason})\n{refused.Explanation}",
        _ => "Issue inconnue",
    };

    /// <summary>Une question, jouee par les deux montages.</summary>
    private sealed class Run
    {
        public Run(QuestionSetItem item, AnswerQuestionResult first, AnswerQuestionResult second)
        {
            Item = item;
            First = first;
            Second = second;
            SameRetrieval = SameFragments(first.Trace.FromIndex, second.Trace.FromIndex)
                            && SameFragments(first.Trace.Supplied, second.Trace.Supplied);
            SameAnswer = string.Equals(
                AskCommand.SummariseOutcome(first.Outcome),
                AskCommand.SummariseOutcome(second.Outcome),
                StringComparison.Ordinal)
                && string.Equals(TextOf(first.Outcome), TextOf(second.Outcome), StringComparison.Ordinal);
        }

        public QuestionSetItem Item { get; }

        public AnswerQuestionResult First { get; }

        public AnswerQuestionResult Second { get; }

        public bool SameRetrieval { get; }

        public bool SameAnswer { get; }

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
}
