using System.Globalization;

using AssistantQR.Cli.Configuration;
using AssistantQR.Cli.Rendering;

using AssistantQR.Application.Ports;
using AssistantQR.Application.UseCases.Snapshots;

using AssistantQR.Domain.Text;

using AssistantQR.Infrastructure.Configuration;

namespace AssistantQR.Cli.Commands;

/// <summary>
/// <c>assistantqr snapshot</c> : enregistrer, lister et comparer des instantanes d'evaluation.
/// </summary>
/// <remarks>
/// POURQUOI CE GROUPE DE COMMANDES EXISTE. Un systeme dont un maillon est probabiliste ne
/// se valide pas par assertion exacte : on ne peut pas ecrire « la reponse doit etre
/// ceci ». On fige donc le comportement du jour sur un jeu de questions fixe, on change UNE
/// chose, et on mesure ce qui a bouge. La commande de comparaison affiche cote a cote les
/// reponses ET les differences de configuration, parce que les deux listes ne veulent rien
/// dire l'une sans l'autre : une derive de 40 % assortie d'un changement de version de
/// prompt est un resultat attendu, la meme derive sans aucune difference de configuration
/// est une alerte.
/// </remarks>
internal static class SnapshotCommand
{
    /// <summary>Options propres au groupe, en plus des surcharges de configuration.</summary>
    public static readonly string[] OwnOptions = { "questions" };

    /// <summary>Aiguille les sous-commandes du groupe « snapshot ».</summary>
    public static Task<int> RunAsync(
        CommandLine commandLine,
        ConsoleRenderer renderer,
        CancellationToken cancellationToken)
    {
        var subCommand = commandLine.Positional(1);

        return subCommand switch
        {
            "record" => RecordAsync(commandLine, renderer, cancellationToken),
            "list" => ListAsync(commandLine, renderer, cancellationToken),
            "compare" => CompareAsync(commandLine, renderer, cancellationToken),
            null => throw new UsageException(
                "Sous-commande manquante. Attendu : snapshot record <nom>, snapshot list, " +
                "ou snapshot compare <reference> <candidat>."),
            _ => throw new UsageException(
                $"Sous-commande inconnue : « snapshot {subCommand} ». " +
                "Attendu : record, list ou compare."),
        };
    }

    private static async Task<int> RecordAsync(
        CommandLine commandLine,
        ConsoleRenderer renderer,
        CancellationToken cancellationToken)
    {
        commandLine.EnsureKnownOptions(CliConfiguration.OverrideOptions.Concat(OwnOptions).ToArray());

        var name = commandLine.Positional(2)
                   ?? throw new UsageException(
                       "Il manque le nom de l'instantane. Exemple : assistantqr snapshot record avant-changement");

        var options = CliConfiguration.Load(commandLine);
        var questions = await QuestionSet
            .LoadAsync(options, commandLine.Value("questions"), cancellationToken)
            .ConfigureAwait(false);

        using var host = CliHost.Create(options);

        renderer.Title($"Instantane « {name} »");
        renderer.Note($"Jeu de questions : {QuestionSet.ResolvePath(options, commandLine.Value("questions"))} " +
                      $"({questions.Count.ToString(CultureInfo.InvariantCulture)} entree(s))");

        renderer.Line();
        await CliHost.EnsureIndexAsync(host, renderer, cancellationToken).ConfigureAwait(false);

        var snapshot = await host.Service<RecordSnapshotUseCase>()
            .ExecuteAsync(new RecordSnapshotCommand(name, questions), cancellationToken)
            .ConfigureAwait(false);

        RenderEntries(renderer, snapshot);

        renderer.Section("Empreinte de configuration enregistree");
        renderer.Paragraph(snapshot.Configuration.ToString());

        renderer.Line();
        renderer.Note($"Ecrit dans {PathResolver.Resolve(options.SnapshotsDirectory)}.");

        return ExitCodes.Success;
    }

    private static async Task<int> ListAsync(
        CommandLine commandLine,
        ConsoleRenderer renderer,
        CancellationToken cancellationToken)
    {
        commandLine.EnsureKnownOptions(CliConfiguration.OverrideOptions);

        var options = CliConfiguration.Load(commandLine);
        using var host = CliHost.Create(options);

        var store = host.Service<ISnapshotStore>();
        var names = await store.ListAsync(cancellationToken).ConfigureAwait(false);

        renderer.Title("Instantanes disponibles");
        renderer.Note($"Dossier : {PathResolver.Resolve(options.SnapshotsDirectory)}");
        renderer.Line();

        if (names.Count == 0)
        {
            renderer.Paragraph(
                "Aucun instantane. Enregistre le premier avec : assistantqr snapshot record reference");
            return ExitCodes.Success;
        }

        var rows = new List<IReadOnlyList<string>>(names.Count);
        var intruders = 0;

        foreach (var name in names)
        {
            EvaluationSnapshot? snapshot;

            try
            {
                snapshot = await store.LoadAsync(name, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException)
            {
                // Le magasin liste tous les fichiers JSON du dossier, y compris le jeu de
                // questions et le fichier de rejeu, qui n'en sont pas. Faire echouer
                // l'inventaire pour cela serait absurde : on le signale et on continue.
                snapshot = null;
                intruders++;
            }

            rows.Add(new[]
            {
                name,
                snapshot?.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                    ?? "(pas un instantane)",
                snapshot?.Entries.Count.ToString(CultureInfo.InvariantCulture) ?? "-",
                snapshot?.Configuration.ToString() ?? "-",
            });
        }

        renderer.Table(new[] { "Nom", "Cree le (UTC)", "Questions", "Configuration" }, rows);

        if (intruders > 0)
        {
            renderer.Line();
            renderer.Note(
                $"{intruders.ToString(CultureInfo.InvariantCulture)} fichier(s) JSON du dossier ne sont pas " +
                "des instantanes : le jeu de questions et le fichier de rejeu y vivent aussi.");
        }

        return ExitCodes.Success;
    }

    private static async Task<int> CompareAsync(
        CommandLine commandLine,
        ConsoleRenderer renderer,
        CancellationToken cancellationToken)
    {
        commandLine.EnsureKnownOptions(CliConfiguration.OverrideOptions);

        var baseline = commandLine.Positional(2)
                       ?? throw new UsageException(
                           "Il manque le nom de l'instantane de reference. " +
                           "Exemple : assistantqr snapshot compare avant apres");

        var candidate = commandLine.Positional(3)
                        ?? throw new UsageException(
                            $"Il manque le nom de l'instantane candidat. " +
                            $"Exemple : assistantqr snapshot compare {baseline} apres");

        var options = CliConfiguration.Load(commandLine);
        using var host = CliHost.Create(options);

        var comparison = await host.Service<CompareSnapshotsUseCase>()
            .ExecuteAsync(baseline, candidate, cancellationToken)
            .ConfigureAwait(false);

        RenderComparison(renderer, comparison);

        return ExitCodes.Success;
    }

    /// <summary>Affiche un rapport de comparaison complet : configuration, derive, ecarts cote a cote.</summary>
    public static void RenderComparison(ConsoleRenderer renderer, SnapshotComparison comparison)
    {
        renderer.Title($"Comparaison « {comparison.BaselineName} » contre « {comparison.CandidateName} »");

        renderer.Section("Differences de configuration");
        if (comparison.ConfigurationDifferences.Count == 0)
        {
            renderer.Note("(aucune : les deux instantanes ont ete pris dans les memes conditions)");
        }
        else
        {
            foreach (var difference in comparison.ConfigurationDifferences)
            {
                renderer.Bullet(difference);
            }
        }

        renderer.Section("Derive");
        renderer.Pairs(new[]
        {
            ("Questions comparees", comparison.TotalCount.ToString(CultureInfo.InvariantCulture)),
            ("Reponses modifiees", comparison.ChangedCount.ToString(CultureInfo.InvariantCulture)),
            ("Taux de derive", ConsoleRenderer.Percent(comparison.DriftRatio)),
        });

        var byKind = comparison.Differences
            .GroupBy(difference => difference.Kind)
            .OrderByDescending(group => group.Key)
            .Select(group => (IReadOnlyList<string>)new[]
            {
                group.Key.ToString(),
                group.Count().ToString(CultureInfo.InvariantCulture),
                DescribeKind(group.Key),
            })
            .ToList();

        renderer.Line();
        renderer.Table(new[] { "Nature", "Nombre", "Lecture" }, byKind);

        var changed = comparison.Differences
            .Where(difference => difference.Kind != DifferenceKind.Identical)
            .ToList();

        renderer.Section("Ecarts, cote a cote");

        if (changed.Count == 0)
        {
            renderer.Note("(aucun : les deux instantanes donnent exactement les memes reponses)");
            return;
        }

        for (var i = 0; i < changed.Count; i++)
        {
            var difference = changed[i];

            renderer.Line();
            renderer.Paragraph(
                $"{(i + 1).ToString(CultureInfo.InvariantCulture)}. " +
                $"{difference.QuestionText}  [{difference.Kind}]");
            renderer.Note(difference.Summary);
            renderer.Line();

            renderer.SideBySide(
                comparison.BaselineName,
                comparison.CandidateName,
                new[] { (DescribeEntry(difference.Baseline), DescribeEntry(difference.Candidate)) });
        }
    }

    /// <summary>Rend un instantane sous forme de tableau question par question.</summary>
    private static void RenderEntries(ConsoleRenderer renderer, EvaluationSnapshot snapshot)
    {
        renderer.Section("Resultats");

        var rows = snapshot.Entries.Select(entry => (IReadOnlyList<string>)new[]
        {
            TextExcerpt.Shorten(entry.QuestionText, 46),
            entry.UserId,
            entry.Clearance,
            entry.Answered ? "reponse" : "refus",
            entry.Answered
                ? string.Join(", ", entry.CitedDocumentIds)
                : entry.RefusalReason ?? "(motif absent)",
        }).ToList();

        renderer.Table(new[] { "Question", "Demandeur", "Habilitation", "Issue", "Sources / motif" }, rows);

        var answered = snapshot.Entries.Count(entry => entry.Answered);
        renderer.Line();
        renderer.Note(
            $"{answered.ToString(CultureInfo.InvariantCulture)} reponse(s), " +
            $"{(snapshot.Entries.Count - answered).ToString(CultureInfo.InvariantCulture)} refus, " +
            $"enregistres au {snapshot.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} UTC.");
    }

    /// <summary>Rend une entree d'instantane sous une forme comparable a l'oeil.</summary>
    private static string DescribeEntry(SnapshotEntry? entry)
    {
        if (entry is null)
        {
            return "(absente de cet instantane)";
        }

        var head = entry.Answered
            ? "Reponse — sources : " + (entry.CitedDocumentIds.Count == 0
                ? "(aucune)"
                : string.Join(", ", entry.CitedDocumentIds))
            : "Refus — " + (entry.RefusalReason ?? "motif absent");

        var scores = entry.Scores.Count == 0
            ? "scores : (aucun extrait retenu)"
            : "scores : " + string.Join(", ", entry.Scores.Select(ConsoleRenderer.Score));

        var body = string.IsNullOrWhiteSpace(entry.AnswerText)
            ? string.Empty
            : "\n" + TextExcerpt.Shorten(entry.AnswerText, 320);

        return head + "\n" + scores + body;
    }

    /// <summary>Ce que chaque nature de divergence signifie pour qui lit le rapport.</summary>
    private static string DescribeKind(DifferenceKind kind) => kind switch
    {
        DifferenceKind.Identical => "rien n'a bouge",
        DifferenceKind.AnswerTextChanged => "memes sources, meme decision : reformulation, la derive la plus benigne",
        DifferenceKind.CitationsChanged => "la reponse ne s'appuie plus sur le meme materiau",
        DifferenceKind.RefusalChanged => "changement de comportement : refus devenu reponse, ou l'inverse",
        DifferenceKind.MissingInBaseline => "question nouvelle dans le candidat",
        DifferenceKind.MissingInCandidate => "question disparue du candidat",
        _ => "nature non decrite par la ligne de commande",
    };
}
