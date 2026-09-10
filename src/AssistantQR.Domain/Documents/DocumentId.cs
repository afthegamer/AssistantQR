namespace AssistantQR.Domain.Documents;

/// <summary>
/// Identifiant stable d'un document du corpus. L'interdiction des espaces n'est pas
/// cosmetique : cet identifiant est repris tel quel dans les citations en ligne
/// <c>[identifiant]</c> imposees au modèle de langue. Une regle de format ici evite
/// une classe entiere de citations ambigues plus loin.
/// </summary>
public readonly record struct DocumentId
{
    private const int MaxLength = 128;

    private readonly string? _value;

    private DocumentId(string value) => _value = value;

    /// <summary>Valeur normalisee (jamais nulle : la valeur par defaut rend une chaine vide).</summary>
    public string Value => _value ?? string.Empty;

    /// <summary>Construit un identifiant apres normalisation.</summary>
    /// <exception cref="DomainException">Si la valeur est vide, dépasse 128 caractères ou contient un espace.</exception>
    public static DocumentId From(string value)
    {
        var trimmed = value?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            throw new DomainException("L'identifiant de document ne peut pas être vide.");
        }

        if (trimmed.Length > MaxLength)
        {
            throw new DomainException(
                $"L'identifiant de document dépasse {MaxLength} caractères : {trimmed.Length}.");
        }

        foreach (var c in trimmed)
        {
            if (char.IsWhiteSpace(c))
            {
                throw new DomainException(
                    $"L'identifiant de document ne peut pas contenir d'espace : « {trimmed} ».");
            }
        }

        return new DocumentId(trimmed);
    }

    /// <inheritdoc />
    public override string ToString() => Value;
}
