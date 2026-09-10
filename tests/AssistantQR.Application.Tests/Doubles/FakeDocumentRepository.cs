using AssistantQR.Application.Ports;

using AssistantQR.Domain.Access;
using AssistantQR.Domain.Documents;

namespace AssistantQR.Application.Tests.Doubles;

/// <summary>
/// Corpus tenu en memoire.
///
/// Le port rend des <see cref="Document"/> deja valides : ni chemin de fichier, ni
/// en-tete YAML, ni traduction du niveau francais « interne » vers
/// <see cref="AccessLevel.Internal"/>. Tout cela reste dans l'adaptateur reel. La
/// consequence pratique se lit ici : la doublure tient en un constructeur, parce que
/// la frontiere a ete placee au bon endroit. Un port qui aurait expose des flux de
/// fichiers obligerait chaque test a fabriquer un dossier temporaire.
/// </summary>
public sealed class FakeDocumentRepository : IDocumentRepository
{
    private readonly List<Document> _documents;

    /// <summary>Construit un depot a partir des documents fournis.</summary>
    public FakeDocumentRepository(params Document[] documents) => _documents = new List<Document>(documents);

    /// <summary>Construit un depot a partir d'une sequence de documents.</summary>
    public FakeDocumentRepository(IEnumerable<Document> documents) => _documents = new List<Document>(documents);

    /// <summary>Confort d'ecriture des tests : un document a partir de chaines.</summary>
    public static Document Doc(string id, string title, AccessLevel level, string content) =>
        new(DocumentId.From(id), title, content, level);

    /// <inheritdoc />
    public Task<IReadOnlyList<Document>> GetAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Document>>(_documents);

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
