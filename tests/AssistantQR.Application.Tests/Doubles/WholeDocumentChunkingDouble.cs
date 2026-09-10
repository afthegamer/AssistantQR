using AssistantQR.Application.Model;
using AssistantQR.Application.Ports;

using AssistantQR.Domain.Documents;

namespace AssistantQR.Application.Tests.Doubles;

/// <summary>
/// Un morceau par document : la strategie de reference la plus grossiere.
///
/// Elle n'est pas la pour etre bonne, elle est la pour etre DIFFERENTE. Comparee au
/// decoupage par paragraphe, elle noie le passage pertinent dans le reste du document
/// et deplace les scores de similarite ; les extraits retenus changent, donc les
/// citations changent. Aucune regle metier n'a bouge.
/// </summary>
public sealed class WholeDocumentChunkingDouble : IChunkingStrategy
{
    /// <inheritdoc />
    public string Id => "fake-whole-document";

    /// <inheritdoc />
    public string Description => "Un seul morceau par document, sans decoupage.";

    /// <inheritdoc />
    public IReadOnlyList<Chunk> Split(Document document) => new[]
    {
        new Chunk(
            Chunk.BuildId(document.Id, 0),
            document.Id,
            document.Title,
            document.AccessLevel,
            0,
            document.Content),
    };
}
