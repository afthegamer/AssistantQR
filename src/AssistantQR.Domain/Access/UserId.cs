namespace AssistantQR.Domain.Access;

/// <summary>
/// Identifiant d'un demandeur. Un <c>string</c> nu conviendrait techniquement, mais
/// il laisserait passer une chaine vide ou un titre de document a la place d'un
/// utilisateur. Le type est la ou l'on paie une fois pour la validation.
/// </summary>
public readonly record struct UserId
{
    private const int MaxLength = 64;

    private readonly string? _value;

    private UserId(string value) => _value = value;

    /// <summary>Valeur normalisee (jamais nulle : la valeur par defaut rend une chaine vide).</summary>
    public string Value => _value ?? string.Empty;

    /// <summary>Construit un identifiant apres normalisation.</summary>
    /// <exception cref="DomainException">Si la valeur est vide ou dépasse 64 caractères.</exception>
    public static UserId From(string value)
    {
        var trimmed = value?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            throw new DomainException("L'identifiant d'utilisateur ne peut pas être vide.");
        }

        if (trimmed.Length > MaxLength)
        {
            throw new DomainException(
                $"L'identifiant d'utilisateur dépasse {MaxLength} caractères : {trimmed.Length}.");
        }

        return new UserId(trimmed);
    }

    /// <inheritdoc />
    public override string ToString() => Value;
}
