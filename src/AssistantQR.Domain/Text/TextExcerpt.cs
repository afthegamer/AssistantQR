namespace AssistantQR.Domain.Text;

/// <summary>
/// Fabrication d'extraits courts. Cette fonction est dans le Domain parce qu'une
/// citation tronquee au milieu d'un mot est un defaut de la REPONSE, pas un detail
/// d'affichage : c'est ce texte-la que l'usager lira pour verifier la source.
/// </summary>
public static class TextExcerpt
{
    private const string Ellipsis = "…";

    /// <summary>
    /// Tronque sur une frontiere de mot et ajoute « … » si tronque. Normalise les blancs
    /// (retours a la ligne et espaces multiples deviennent un espace simple).
    /// La longueur demandee s'applique au texte conserve, avant ajout des points de suspension.
    /// </summary>
    public static string Shorten(string text, int maxLength)
    {
        var normalized = NormalizeWhitespace(text);

        if (maxLength <= 0 || normalized.Length == 0)
        {
            return string.Empty;
        }

        if (normalized.Length <= maxLength)
        {
            return normalized;
        }

        var cut = normalized.Substring(0, maxLength);
        var lastSpace = cut.LastIndexOf(' ');
        if (lastSpace > 0)
        {
            cut = cut.Substring(0, lastSpace);
        }

        cut = cut.TrimEnd();

        return cut.Length == 0 ? Ellipsis : cut + Ellipsis;
    }

    private static string NormalizeWhitespace(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder(text.Length);
        var previousWasBlank = false;

        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                previousWasBlank = true;
                continue;
            }

            if (previousWasBlank && builder.Length > 0)
            {
                builder.Append(' ');
            }

            previousWasBlank = false;
            builder.Append(c);
        }

        return builder.ToString();
    }
}
