using System.Globalization;

using AssistantQR.Cli.Configuration;
using AssistantQR.Cli.Rendering;

using AssistantQR.Application.Ports;
using AssistantQR.Application.UseCases.IndexCorpus;

namespace AssistantQR.Cli.Commands;

/// <summary>
/// <c>assistantqr index</c> : (re)construit l'index vectoriel a partir du corpus.
/// </summary>
/// <remarks>
/// LA COMMANDE AFFICHE LE MODELE ET LE DECOUPAGE, PAS SEULEMENT LE NOMBRE DE MORCEAUX.
/// Une indexation n'est pas une operation neutre : elle fige dans l'index un espace
/// vectoriel et une facon de couper le texte. Six mois plus tard, quand les reponses
/// deriveront, la seule question utile sera « sous quelles conditions cet index a-t-il
/// ete construit ». Les afficher ici, c'est donner a l'operateur l'occasion de les noter
/// — et c'est la meme information que <c>GetMetadataAsync</c> rendra au pipeline.
/// </remarks>
internal static class IndexCommand
{
    /// <summary>Reindexe le corpus et rend compte de l'operation.</summary>
    public static async Task<int> RunAsync(
        CommandLine commandLine,
        ConsoleRenderer renderer,
        CancellationToken cancellationToken)
    {
        commandLine.EnsureKnownOptions(CliConfiguration.OverrideOptions);

        var options = CliConfiguration.Load(commandLine);
        using var host = CliHost.Create(options);

        renderer.Title("Indexation du corpus");

        var chunking = host.Service<IChunkingStrategy>();
        var result = await host.Service<IndexCorpusUseCase>().ExecuteAsync(cancellationToken).ConfigureAwait(false);

        renderer.Pairs(new[]
        {
            ("Documents", result.DocumentCount.ToString(CultureInfo.InvariantCulture)),
            ("Morceaux", result.ChunkCount.ToString(CultureInfo.InvariantCulture)),
            ("Modele d'embeddings", $"{result.EmbeddingModel} (dimension {result.Dimension})"),
            ("Decoupage", $"{result.ChunkingStrategyId} — {chunking.Description}"),
            ("Duree totale", ConsoleRenderer.Duration(result.Duration)),
            ("Dont embeddings", ConsoleRenderer.Duration(result.EmbeddingDuration)),
        });

        renderer.Line();

        if (options.IsOffline)
        {
            renderer.Paragraph(
                "Profil « offline » : cet index vit dans la memoire du processus et disparait avec lui. " +
                "Les commandes ask, snapshot et demo le reconstruisent d'elles-memes au demarrage. En profil " +
                "« local », l'index reste dans le service Python et cette commande devient necessaire.");
        }
        else
        {
            renderer.Paragraph(
                "L'index est desormais construit avec le modele et le decoupage ci-dessus. Interroger cet " +
                "index avec un AUTRE modele d'embeddings ne produira aucune erreur, seulement des scores " +
                "denues de sens : c'est la panne que la commande « demo embedding-swap » met en scene.");
        }

        return ExitCodes.Success;
    }
}
