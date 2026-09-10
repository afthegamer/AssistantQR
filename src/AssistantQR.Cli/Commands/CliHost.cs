using AssistantQR.Cli.Rendering;

using AssistantQR.Application.Ports;
using AssistantQR.Application.UseCases.IndexCorpus;

using AssistantQR.Infrastructure.Configuration;
using AssistantQR.Infrastructure.DependencyInjection;

using Microsoft.Extensions.DependencyInjection;

namespace AssistantQR.Cli.Commands;

/// <summary>
/// Un conteneur monte a partir d'un jeu de reglages, et les quelques gestes que toutes
/// les commandes partagent.
/// </summary>
/// <remarks>
/// LE MONTAGE EST AILLEURS. Cette classe n'enregistre aucun adaptateur : elle appelle
/// <c>AddAssistantQR</c> et s'arrete la. C'est important pour le cours — la racine de
/// composition doit rester un seul fichier, lisible d'un trait, et la presentation ne
/// doit pas etre un second endroit ou l'on decide « quelle piece concrete branche-t-on ».
///
/// POURQUOI ON SAIT CONSTRUIRE PLUSIEURS CONTENEURS. Les demonstrations comparent deux
/// configurations : deux modeles de generation, deux modeles d'embeddings, deux modes de
/// filtrage. Chaque variante est un montage complet, obtenu en changeant des REGLAGES et
/// rien d'autre — c'est exactement la these du scenario A, rendue executable. Le crochet
/// <paramref name="share"/> permet de reinjecter dans le second montage une instance deja
/// construite par le premier : c'est ce qui rend verifiable la phrase « on a change de
/// modele de langue SANS reindexer », puisque les deux montages interrogent alors
/// litteralement le meme index.
/// </remarks>
internal sealed class CliHost : IDisposable
{
    private readonly ServiceProvider _provider;

    private CliHost(ServiceProvider provider, AssistantOptions options)
    {
        _provider = provider;
        Options = options;
    }

    /// <summary>Les reglages effectifs de ce montage.</summary>
    public AssistantOptions Options { get; }

    /// <summary>Monte le conteneur pour un jeu de reglages donne.</summary>
    /// <param name="options">Reglages issus du fichier, de l'environnement et de la ligne de commande.</param>
    /// <param name="share">
    /// Enregistrements appliques APRES le montage standard, donc prioritaires : sert a
    /// partager une instance entre deux variantes d'une meme demonstration.
    /// </param>
    public static CliHost Create(AssistantOptions options, Action<IServiceCollection>? share = null)
    {
        var services = new ServiceCollection();
        services.AddAssistantQR(options);
        share?.Invoke(services);

        return new CliHost(services.BuildServiceProvider(), options);
    }

    /// <summary>Resout un service obligatoire.</summary>
    public T Service<T>()
        where T : notnull => _provider.GetRequiredService<T>();

    /// <summary>Resout un service en tolerant son absence ou son echec de construction.</summary>
    public object? TryResolve(Type serviceType)
    {
        try
        {
            return _provider.GetService(serviceType);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException
                                              or IOException or UnauthorizedAccessException)
        {
            // Un catalogue de gabarits absent ne doit pas empecher d'AFFICHER la liste des
            // ports : la commande « ports » est un outil de lecture, pas d'execution.
            return null;
        }
    }

    /// <summary>
    /// Garantit qu'un index interrogeable existe, et le construit sinon.
    /// </summary>
    /// <remarks>
    /// EN PROFIL HORS LIGNE, L'INDEX VIT DANS LA MEMOIRE DU PROCESSUS : il disparait a
    /// chaque sortie du programme. Une commande <c>ask</c> qui supposerait un index
    /// deja construit echouerait donc systematiquement, et l'etudiant conclurait que le
    /// depot est casse. On indexe a la volee, en le disant. En profil local, l'index vit
    /// dans le service Python : la sonde le trouve peuple et cette methode ne fait rien.
    /// </remarks>
    public static async Task<IndexCorpusResult?> EnsureIndexAsync(
        CliHost host,
        ConsoleRenderer renderer,
        CancellationToken cancellationToken)
    {
        var metadata = await host.Service<IVectorIndex>().GetMetadataAsync(cancellationToken).ConfigureAwait(false);

        if (metadata.ChunkCount > 0)
        {
            return null;
        }

        renderer.Note("Index vide : indexation du corpus avant de poursuivre.");

        var result = await host.Service<IndexCorpusUseCase>().ExecuteAsync(cancellationToken).ConfigureAwait(false);

        renderer.Note(
            $"{result.ChunkCount} morceau(x) issus de {result.DocumentCount} document(s), " +
            $"decoupage « {result.ChunkingStrategyId} », modele « {result.EmbeddingModel} », " +
            $"en {ConsoleRenderer.Duration(result.Duration)}.");

        return result;
    }

    /// <summary>Libere le conteneur et les instances qu'il a construites.</summary>
    public void Dispose() => _provider.Dispose();
}
