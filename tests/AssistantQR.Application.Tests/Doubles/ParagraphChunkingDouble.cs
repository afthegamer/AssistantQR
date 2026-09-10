using AssistantQR.Application.Model;
using AssistantQR.Application.Ports;

using AssistantQR.Domain.Documents;

namespace AssistantQR.Application.Tests.Doubles;

/// <summary>
/// Decoupage sur les lignes vides : un paragraphe, un morceau.
///
/// C'est la strategie de reference des tests, et son jumeau
/// <see cref="WholeDocumentChunkingDouble"/> sert a demontrer le principe CACE :
/// meme corpus, meme modele, meme prompt, meme question — et des reponses
/// differentes, uniquement parce que la granularite des morceaux a change. Le
/// decoupage ne figure dans aucune regle metier, et pourtant il commande ce que le
/// modele voit. C'est la raison d'etre de <c>ChunkingStrategyId</c> dans l'empreinte
/// de configuration.
/// </summary>
public sealed class ParagraphChunkingDouble : IChunkingStrategy
{
    /// <inheritdoc />
    public string Id => "fake-paragraph";

    /// <inheritdoc />
    public string Description => "Un morceau par paragraphe, separe par une ligne vide.";

    /// <inheritdoc />
    public IReadOnlyList<Chunk> Split(Document document)
    {
        var normalized = document.Content.Replace("\r\n", "\n", StringComparison.Ordinal);
        var blocks = normalized.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);

        var chunks = new List<Chunk>(blocks.Length);
        var ordinal = 0;

        foreach (var block in blocks)
        {
            var text = block.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            chunks.Add(new Chunk(
                Chunk.BuildId(document.Id, ordinal),
                document.Id,
                document.Title,
                document.AccessLevel,
                ordinal,
                text));

            ordinal++;
        }

        return chunks;
    }
}
