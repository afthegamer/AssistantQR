using AssistantQR.Domain.Access;

namespace AssistantQR.Domain.Documents;

/// <summary>
/// ENTITE : identite stable, egalite par Id. Contraste volontaire avec les
/// value objects du dossier voisin, qui sont egaux par valeur. Deux versions
/// successives d'un meme document (titre corrige, texte reecrit) restent le
/// meme document ; deux <see cref="DocumentId"/> de meme valeur sont, eux,
/// litteralement la meme chose.
/// </summary>
public sealed class Document : IEquatable<Document>
{
    /// <summary>Construit un document et verifie ses invariantes.</summary>
    /// <exception cref="DomainException">Si le titre ou le contenu est vide.</exception>
    public Document(
        DocumentId id,
        string title,
        string content,
        AccessLevel accessLevel,
        IReadOnlyList<string>? tags = null)
    {
        var normalizedTitle = title?.Trim() ?? string.Empty;
        if (normalizedTitle.Length == 0)
        {
            throw new DomainException($"Le document « {id} » n'a pas de titre.");
        }

        var normalizedContent = content?.Trim() ?? string.Empty;
        if (normalizedContent.Length == 0)
        {
            throw new DomainException($"Le document « {id} » n'a pas de contenu.");
        }

        Id = id;
        Title = normalizedTitle;
        Content = normalizedContent;
        AccessLevel = accessLevel;
        Tags = NormalizeTags(tags);
    }

    /// <summary>Identite du document.</summary>
    public DocumentId Id { get; }

    /// <summary>Titre lisible, repris dans les citations.</summary>
    public string Title { get; }

    /// <summary>Texte integral du document.</summary>
    public string Content { get; }

    /// <summary>Habilitation minimale requise pour lire ce document.</summary>
    public AccessLevel AccessLevel { get; }

    /// <summary>Etiquettes libres, jamais nulles.</summary>
    public IReadOnlyList<string> Tags { get; }

    /// <summary>Ce demandeur a-t-il le droit de lire ce document ?</summary>
    public bool IsReadableBy(Requester requester) => AccessLevel.IsReadableWith(requester.Clearance);

    /// <inheritdoc />
    public bool Equals(Document? other) => other is not null && Id.Equals(other.Id);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as Document);

    /// <inheritdoc />
    public override int GetHashCode() => Id.GetHashCode();

    /// <inheritdoc />
    public override string ToString() => $"{Id} — {Title} ({AccessLevel})";

    private static IReadOnlyList<string> NormalizeTags(IReadOnlyList<string>? tags)
    {
        if (tags is null || tags.Count == 0)
        {
            return Array.Empty<string>();
        }

        var result = new List<string>(tags.Count);
        foreach (var tag in tags)
        {
            var trimmed = tag?.Trim();
            if (!string.IsNullOrEmpty(trimmed))
            {
                result.Add(trimmed);
            }
        }

        return result;
    }
}
