namespace AssistantQR.Domain.Questions;

/// <summary>
/// La question posee, normalisee et bornee.
/// Constructeur prive + fabrique statique : avec un record positionnel, un appelant
/// pourrait ecrire <c>new Question("")</c> ou contourner la validation avec
/// <c>with</c>. La validation n'a de valeur que si elle est incontournable.
/// </summary>
public sealed record Question
{
    private const int MaxLength = 2000;

    private Question(string text) => Text = text;

    /// <summary>Texte normalise de la question.</summary>
    public string Text { get; }

    /// <summary>Construit une question apres normalisation.</summary>
    /// <exception cref="DomainException">Si le texte est vide ou dépasse 2000 caractères.</exception>
    public static Question From(string text)
    {
        var trimmed = text?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            throw new DomainException("La question ne peut pas être vide.");
        }

        if (trimmed.Length > MaxLength)
        {
            throw new DomainException(
                $"La question dépasse {MaxLength} caractères : {trimmed.Length}.");
        }

        return new Question(trimmed);
    }

    /// <inheritdoc />
    public override string ToString() => Text;
}
