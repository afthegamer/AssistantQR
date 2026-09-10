using System.Globalization;

using AssistantQR.Cli.Configuration;
using AssistantQR.Cli.Rendering;

using AssistantQR.Application.Ports;

using AssistantQR.Domain.Access;

using AssistantQR.Infrastructure.Configuration;

namespace AssistantQR.Cli.Commands;

/// <summary>
/// <c>assistantqr corpus list</c> : ce que le systeme a le droit de lire, et pour qui.
/// </summary>
/// <remarks>
/// La commande passe par <c>IDocumentRepository</c> et non par le systeme de fichiers. La
/// difference n'est pas cosmetique : ce qui s'affiche ici est exactement ce que le
/// pipeline verra, en-tetes deja analyses et niveaux francais deja traduits. Une commande
/// qui listerait les fichiers du dossier montrerait un corpus qui n'existe pas — celui
/// d'avant la frontiere.
/// </remarks>
internal static class CorpusCommand
{
    /// <summary>Aiguille les sous-commandes du groupe « corpus ».</summary>
    public static Task<int> RunAsync(
        CommandLine commandLine,
        ConsoleRenderer renderer,
        CancellationToken cancellationToken)
    {
        var subCommand = commandLine.Positional(1);

        return subCommand switch
        {
            "list" or null => ListAsync(commandLine, renderer, cancellationToken),
            _ => throw new UsageException(
                $"Sous-commande inconnue : « corpus {subCommand} ». Seule « corpus list » existe."),
        };
    }

    private static async Task<int> ListAsync(
        CommandLine commandLine,
        ConsoleRenderer renderer,
        CancellationToken cancellationToken)
    {
        commandLine.EnsureKnownOptions(CliConfiguration.OverrideOptions);

        var options = CliConfiguration.Load(commandLine);
        using var host = CliHost.Create(options);

        var documents = await host.Service<IDocumentRepository>()
            .GetAllAsync(cancellationToken)
            .ConfigureAwait(false);

        renderer.Title("Corpus");
        renderer.Note($"Source : {PathResolver.Resolve(options.CorpusDirectory)}");

        var ordered = documents
            .OrderBy(document => document.AccessLevel.Rank)
            .ThenBy(document => document.Id.Value, StringComparer.Ordinal)
            .ToList();

        renderer.Line();
        renderer.Table(
            new[] { "Identifiant", "Niveau", "Signes", "Titre" },
            ordered.Select(document => (IReadOnlyList<string>)new[]
            {
                document.Id.Value,
                document.AccessLevel.Name,
                document.Content.Length.ToString(CultureInfo.InvariantCulture),
                document.Title,
            }).ToList());

        renderer.Section("Repartition par niveau");
        renderer.Table(
            new[] { "Niveau", "Documents", "Lisible par une habilitation" },
            AccessLevel.All.Select(level => (IReadOnlyList<string>)new[]
            {
                level.Name,
                ordered.Count(document => document.AccessLevel == level).ToString(CultureInfo.InvariantCulture),
                DescribeReaders(level),
            }).ToList());

        renderer.Line();
        renderer.Paragraph(
            "Le niveau vient de l'en-tete du fichier, ecrit en francais (« interne »), et il a ete traduit " +
            "en AccessLevel par l'adaptateur de lecture. Le Domain ne connait que les noms anglais : le " +
            "vocabulaire du monde exterieur s'arrete a la frontiere.");

        return ExitCodes.Success;
    }

    private static string DescribeReaders(AccessLevel level)
    {
        var readers = AccessLevel.All
            .Where(clearance => level.IsReadableWith(clearance))
            .Select(clearance => clearance.Name);

        return string.Join(", ", readers);
    }
}
