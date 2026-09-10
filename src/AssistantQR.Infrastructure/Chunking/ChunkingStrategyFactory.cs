using System.Globalization;

using AssistantQR.Application.Ports;

namespace AssistantQR.Infrastructure.Chunking;

/// <summary>
/// Construit une strategie de decoupage a partir de son identifiant textuel.
/// </summary>
/// <remarks>
/// AVERTISSEMENT — PRINCIPE CACE (<i>Changing Anything Changes Everything</i>).
/// La chaine passee a <see cref="Create"/> vient d'un fichier de configuration. Y
/// remplacer « paragraph » par « fixed-600-100 » ne casse aucune compilation, ne
/// declenche aucun avertissement, ne fait echouer aucun test de type — et change
/// TOUTES les reponses du systeme. Les morceaux changent, donc les vecteurs, donc les
/// resultats de recherche, donc les extraits envoyes au modele, donc le texte produit
/// et les documents cites. Une reponse jusque-la correcte peut devenir un refus, et
/// inversement.
///
/// C'est la difference de nature entre un systeme classique et un systeme a base
/// d'apprentissage : dans le premier, les modules ont des contrats verifiables et l'on
/// peut raisonner localement ; dans le second, tout reglage numerique est couple a
/// tous les autres par les donnees. Le compilateur, qui verifie des types, n'a
/// strictement rien a dire sur ce couplage-la.
///
/// TROIS CONTRE-MESURES DANS CE PROJET, ET AUCUNE N'EMPECHE LA DERIVE — elles la
/// rendent visible : l'identifiant de la strategie est enregistre dans les
/// metadonnees de l'index ; il entre dans l'empreinte de configuration des
/// instantanes ; la comparaison d'instantanes annonce « ChunkingStrategyId :
/// paragraph → fixed-600-100 » a cote de la liste des reponses qui ont change.
/// Un reglage attribuable vaut mieux qu'un reglage interdit.
/// </remarks>
public static class ChunkingStrategyFactory
{
    private const string ValidIdentifiers =
        "paragraph, whole-document, fixed, fixed-<taille>-<recouvrement> (exemple : fixed-600-100)";

    /// <summary>Instancie la strategie designee par <paramref name="id"/>.</summary>
    /// <exception cref="ArgumentException">Si l'identifiant n'est pas reconnu.</exception>
    public static IChunkingStrategy Create(string id)
    {
        var normalized = id?.Trim().ToLowerInvariant() ?? string.Empty;

        switch (normalized)
        {
            case "paragraph":
                return new ParagraphChunkingStrategy();
            case "whole-document":
                return new WholeDocumentChunkingStrategy();
            case "fixed":
                return new FixedSizeChunkingStrategy();
            default:
                break;
        }

        if (TryParseFixed(normalized, out var size, out var overlap))
        {
            return new FixedSizeChunkingStrategy(size, overlap);
        }

        throw new ArgumentException(
            $"Stratégie de découpage inconnue : « {id} ». Valeurs acceptées : {ValidIdentifiers}.",
            nameof(id));
    }

    private static bool TryParseFixed(string normalized, out int size, out int overlap)
    {
        size = 0;
        overlap = 0;

        var parts = normalized.Split('-');
        if (parts.Length != 3 || parts[0] != "fixed")
        {
            return false;
        }

        return int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out size)
            && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out overlap);
    }
}
