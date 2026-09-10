using AssistantQR.Domain.Documents;

namespace AssistantQR.Domain.Answers;

/// <summary>
/// REGLE METIER 1 : une réponse SANS citation n'existe pas. L'invariante est portee
/// par le type lui-meme — il est impossible d'en construire une qui la viole.
/// Aucun garde-fou en aval (validateur, test d'integration, revue de code) n'est
/// aussi fiable qu'un constructeur prive.
/// </summary>
public sealed record Answer
{
    private Answer(string text, IReadOnlyList<Citation> citations, IReadOnlyList<DocumentId> citedDocumentIds)
    {
        Text = text;
        Citations = citations;
        CitedDocumentIds = citedDocumentIds;
    }

    /// <summary>Texte de la réponse, non vide.</summary>
    public string Text { get; }

    /// <summary>Sources attachees, au moins une, sans doublon (document, fragment).</summary>
    public IReadOnlyList<Citation> Citations { get; }

    /// <summary>Documents cites, distincts, dans l'ordre d'apparition.</summary>
    public IReadOnlyList<DocumentId> CitedDocumentIds { get; }

    /// <summary>Seule voie de construction d'une réponse.</summary>
    /// <exception cref="DomainException">
    /// Si le texte est vide, si la liste de citations est nulle ou vide, ou si deux
    /// citations designent le meme couple (document, fragment).
    /// </exception>
    public static Answer Create(string text, IReadOnlyList<Citation> citations)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw new DomainException("Une réponse ne peut pas avoir un texte vide.");
        }

        if (citations is null || citations.Count == 0)
        {
            throw new DomainException(
                "Une réponse sans citation n'est pas une réponse : au moins une source est exigée.");
        }

        var seenFragments = new HashSet<(string DocumentId, int Ordinal)>();
        var citedDocumentIds = new List<DocumentId>(citations.Count);
        var seenDocuments = new HashSet<string>(StringComparer.Ordinal);
        var frozen = new List<Citation>(citations.Count);

        foreach (var citation in citations)
        {
            if (citation is null)
            {
                throw new DomainException("Une citation nulle a été fournie à la réponse.");
            }

            if (!seenFragments.Add((citation.DocumentId.Value, citation.ChunkOrdinal)))
            {
                throw new DomainException(
                    $"Citation en double pour le document « {citation.DocumentId} », fragment {citation.ChunkOrdinal}.");
            }

            if (seenDocuments.Add(citation.DocumentId.Value))
            {
                citedDocumentIds.Add(citation.DocumentId);
            }

            frozen.Add(citation);
        }

        return new Answer(trimmed, frozen, citedDocumentIds);
    }
}
