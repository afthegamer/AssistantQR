using AssistantQR.Application.Model;

namespace AssistantQR.Application.Tests.Doubles;

/// <summary>
/// Fabrique de vecteurs a deux composantes dont la similarite cosinus avec
/// <see cref="Query"/> vaut EXACTEMENT le score demande.
///
/// POURQUOI CE DETOUR PLUTOT QU'UN INDEX QUI RENDRAIT DES SCORES EN DUR.
/// Les tests de strategie de recuperation ont besoin de scores choisis a la main
/// (0.95, 0.88, 0.70...) pour construire des situations precises. On pourrait
/// scripter un faux index qui recracherait ces nombres, mais on ne testerait alors
/// plus rien du tri ni du cosinus : le test verifierait la doublure.
/// Ici, l'index factice calcule un vrai cosinus sur de vrais vecteurs ; c'est la
/// GEOMETRIE qui est choisie, pas le resultat. Avec q = (1, 0) et
/// v = (s, racine(1 - s²)), les deux vecteurs sont unitaires et leur produit
/// scalaire vaut s.
/// </summary>
public static class UnitVectors
{
    /// <summary>Le vecteur de requete de reference : l'axe des abscisses.</summary>
    public static EmbeddingVector Query { get; } = EmbeddingVector.From(new[] { 1f, 0f });

    /// <summary>Un vecteur unitaire dont le cosinus avec <see cref="Query"/> vaut <paramref name="score"/>.</summary>
    public static EmbeddingVector WithCosine(double score)
    {
        var clamped = Math.Clamp(score, -1d, 1d);
        return EmbeddingVector.From(new[] { (float)clamped, (float)Math.Sqrt(1d - (clamped * clamped)) });
    }
}
