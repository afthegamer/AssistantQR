using AssistantQR.Application.Ports;
using AssistantQR.Domain.Documents;

namespace AssistantQR.Infrastructure.Corpus;

/// <summary>
/// Adaptateur FACTICE de <see cref="IDocumentRepository"/> : un corpus donne a la main.
/// </summary>
/// <remarks>
/// POURQUOI UN DOUBLE EN PLUS DU DEPOT REEL. Ecrire un test sur trois documents choisis
/// — un public, un interne, un confidentiel qui parlent du meme sujet — demande de
/// controler exactement leur contenu. Passer par le disque obligerait a fabriquer des
/// fichiers temporaires, donc a tester le systeme de fichiers en meme temps que la
/// regle qu'on voulait verifier. Le double supprime cette confusion : il ne simule pas
/// le depot reel, il occupe la meme place dans le montage.
///
/// Il n'y a rien a « faire semblant » ici, et c'est la le point : quand une frontiere
/// est bien placee, son double tient en quelques lignes. Un double complique est le
/// symptome d'un port qui en demande trop.
/// </remarks>
public sealed class InMemoryDocumentRepository : IDocumentRepository
{
    private readonly IReadOnlyList<Document> _documents;

    /// <summary>Construit le depot a partir d'une sequence de documents.</summary>
    public InMemoryDocumentRepository(IEnumerable<Document> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);

        var materialized = new List<Document>();
        foreach (var document in documents)
        {
            if (document is not null)
            {
                materialized.Add(document);
            }
        }

        // L'ordre fourni est conserve tel quel, alors que le depot reel trie par
        // identifiant : un test doit pouvoir imposer l'ordre qu'il veut observer.
        _documents = materialized;
    }

    /// <summary>Surcharge de confort pour l'ecriture des tests.</summary>
    public InMemoryDocumentRepository(params Document[] documents)
        : this((IEnumerable<Document>)documents)
    {
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Document>> GetAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_documents);

    /// <inheritdoc />
    public Task<Document?> FindAsync(DocumentId id, CancellationToken cancellationToken = default)
    {
        foreach (var document in _documents)
        {
            if (document.Id.Equals(id))
            {
                return Task.FromResult<Document?>(document);
            }
        }

        return Task.FromResult<Document?>(null);
    }
}
