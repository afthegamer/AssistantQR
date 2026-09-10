using AssistantQR.Domain.Access;
using AssistantQR.Domain.Documents;
using AssistantQR.Domain.Evidence;
using AssistantQR.Domain.Text;

namespace AssistantQR.Domain.Answers;

/// <summary>
/// Une source attachee a une réponse. Elle duplique volontairement le titre et le
/// niveau du document : une réponse doit rester verifiable telle quelle, sans
/// relire le corpus. C'est une copie assumee, pas une reference paresseuse.
/// </summary>
public sealed record Citation(
    DocumentId DocumentId,
    string DocumentTitle,
    string Excerpt,
    AccessLevel AccessLevel,
    int ChunkOrdinal)
{
    /// <summary>Transforme un fragment retenu en citation, en raccourcissant son texte.</summary>
    public static Citation FromEvidence(EvidenceFragment fragment, int excerptLength = 240) =>
        new(
            fragment.DocumentId,
            fragment.DocumentTitle,
            TextExcerpt.Shorten(fragment.Text, excerptLength),
            fragment.AccessLevel,
            fragment.Ordinal);
}
