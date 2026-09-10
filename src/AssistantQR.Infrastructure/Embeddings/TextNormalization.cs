using System.Globalization;
using System.Text;

namespace AssistantQR.Infrastructure.Embeddings;

/// <summary>
/// Repliement de texte francais : minuscules et suppression des signes diacritiques.
/// </summary>
/// <remarks>
/// POURQUOI CE CODE EST EN INFRASTRUCTURE : « mediatheque » et « mediathèque » designent
/// la meme chose pour un humain, mais pas pour un hachage. Decider qu'on les confond est
/// un choix de mecanique de recherche, pas une regle metier — le Domain n'en sait rien.
///
/// ATTENTION, PIEGE DE PLATEFORME (et c'est le genre de detail qui coute une soiree) :
/// le depot compile avec <c>InvariantGlobalization=true</c>. Dans ce mode, la methode
/// <c>string.Normalize(FormD)</c> ne decompose RIEN : elle rend la chaine telle quelle,
/// sans lever d'exception. La recette classique « FormD puis on jette les NonSpacingMark »
/// devient donc un silencieux no-op, et « hôtel » ne rejoint jamais « hotel ».
/// On garde la decomposition — utile la ou elle fonctionne, et elle traite les accents
/// deja decomposes — mais on la double d'une table de repliement explicite pour le
/// latin-1, qui couvre tout le francais et ne depend d'aucune donnee de culture.
///
/// Cet utilitaire est partage par l'embedding factice et par le modele de rejeu :
/// les deux doivent replier le texte EXACTEMENT de la meme facon, sinon un jeu de rejeu
/// ecrit sans accents ne retrouverait pas une question qui en porte.
/// </remarks>
internal static class TextNormalization
{
    // Les deux chaines sont alignees caractere par caractere : Accented[i] se replie en Plain[i].
    private const string Accented = "àáâãäåçèéêëìíîïñòóôõöùúûüýÿÀÁÂÃÄÅÇÈÉÊËÌÍÎÏÑÒÓÔÕÖÙÚÛÜÝ";
    private const string Plain = "aaaaaaceeeeiiiinooooouuuuyyAAAAAACEEEEIIIINOOOOOUUUUY";

    /// <summary>Minuscules puis suppression des accents. C'est l'ordre impose par le contrat.</summary>
    internal static string Fold(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : RemoveDiacritics(text.ToLowerInvariant());

    /// <summary>Supprime les signes diacritiques sans toucher a la casse.</summary>
    internal static string RemoveDiacritics(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var decomposed = TryDecompose(text);
        var builder = new StringBuilder(decomposed.Length);

        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            var index = Accented.IndexOf(c);
            if (index >= 0)
            {
                builder.Append(Plain[index]);
                continue;
            }

            // Ligatures et eszett : un caractere se replie en deux lettres.
            switch (c)
            {
                case 'œ':
                    builder.Append("oe");
                    break;
                case 'Œ':
                    builder.Append("OE");
                    break;
                case 'æ':
                    builder.Append("ae");
                    break;
                case 'Æ':
                    builder.Append("AE");
                    break;
                case 'ß':
                    builder.Append("ss");
                    break;
                case 'Ÿ':
                    builder.Append('Y');
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }

        return builder.ToString();
    }

    private static string TryDecompose(string text)
    {
        try
        {
            return text.Normalize(NormalizationForm.FormD);
        }
        catch (ArgumentException)
        {
            // Chaine contenant des unites de substitution invalides : on continue sans
            // decomposer plutot que de faire echouer une indexation entiere.
            return text;
        }
    }
}
