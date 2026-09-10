using AssistantQR.Domain.Text;
using Xunit;

namespace AssistantQR.Domain.Tests;

/// <summary>
/// Couper un extrait au milieu d'un mot n'est pas un defaut d'affichage : c'est ce
/// texte-la que l'usager lira pour verifier la source d'une réponse. La troncature
/// appartient donc au Domain, et son comportement merite d'etre fige par des tests.
/// </summary>
public sealed class TextExcerptTests
{
    private const string Ellipsis = "…";

    [Fact]
    public void Shorten_TextePlusCourtQueLaLimite_RendLeTexteInchangeSansPointsDeSuspension()
    {
        var resultat = TextExcerpt.Shorten("Un extrait court.", 240);

        Assert.Equal("Un extrait court.", resultat);
        Assert.DoesNotContain(Ellipsis, resultat);
    }

    [Fact]
    public void Shorten_TexteExactementALaLimite_RendLeTexteInchange()
    {
        var texte = new string('a', 40);

        Assert.Equal(texte, TextExcerpt.Shorten(texte, 40));
    }

    /// <summary>
    /// Frontiere de mot : on recule jusqu'au dernier espace contenu dans la fenetre
    /// plutot que de couper « dor|t ». La citation reste lisible.
    /// </summary>
    [Fact]
    public void Shorten_TexteTropLong_CoupeSurLaDerniereFrontiereDeMot()
    {
        var resultat = TextExcerpt.Shorten("Le chat dort sur le tapis rouge.", 10);

        Assert.Equal("Le chat" + Ellipsis, resultat);
    }

    [Fact]
    public void Shorten_TexteTropLong_NeSeTermineJamaisParUnEspaceAvantLesPointsDeSuspension()
    {
        var resultat = TextExcerpt.Shorten("abcd efghijklmnop", 5);

        Assert.Equal("abcd" + Ellipsis, resultat);
    }

    /// <summary>
    /// Cas degrade assume : si aucun espace n'apparait dans la fenetre, on coupe net.
    /// Mieux vaut un extrait tronque au caractere pres qu'un extrait vide.
    /// </summary>
    [Fact]
    public void Shorten_AucuneFrontiereDeMotDansLaFenetre_CoupeAuCaractere()
    {
        var resultat = TextExcerpt.Shorten("abcdefghijklmnop qrs", 6);

        Assert.Equal("abcdef" + Ellipsis, resultat);
    }

    [Fact]
    public void Shorten_TexteTropLong_SeTermineParDesPointsDeSuspension()
    {
        var resultat = TextExcerpt.Shorten(string.Join(" ", Enumerable.Repeat("mot", 100)), 50);

        Assert.EndsWith(Ellipsis, resultat);
        Assert.True(resultat.Length <= 51, $"Extrait trop long : {resultat.Length} caractères.");
    }

    // --- Normalisation des blancs -------------------------------------------

    [Fact]
    public void Shorten_TexteAvecSautsDeLigneEtEspacesMultiples_NormaliseLesBlancs()
    {
        var resultat = TextExcerpt.Shorten("Premier paragraphe.\n\n  Deuxième\tparagraphe.   Fin.", 240);

        Assert.Equal("Premier paragraphe. Deuxième paragraphe. Fin.", resultat);
    }

    [Fact]
    public void Shorten_TexteEntoureDeBlancs_LesRetire()
    {
        Assert.Equal("Contenu utile.", TextExcerpt.Shorten("   \n Contenu utile. \t  ", 240));
    }

    /// <summary>La limite s'applique au texte NORMALISE, pas au texte brut.</summary>
    [Fact]
    public void Shorten_BlancsMultiplesAvantLaLimite_LaLimitePorteSurLeTexteNormalise()
    {
        var resultat = TextExcerpt.Shorten("aa     bb     cc", 6);

        Assert.Equal("aa bb" + Ellipsis, resultat);
    }

    // --- Cas limites ---------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Shorten_LimiteNulleOuNegative_RendUneChaineVide(int maxLength)
    {
        Assert.Equal(string.Empty, TextExcerpt.Shorten("Un texte quelconque.", maxLength));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t  \r\n")]
    public void Shorten_TexteVideOuBlanc_RendUneChaineVide(string text)
    {
        Assert.Equal(string.Empty, TextExcerpt.Shorten(text, 240));
    }

    [Fact]
    public void Shorten_TexteNul_RendUneChaineVideSansLever()
    {
        Assert.Equal(string.Empty, TextExcerpt.Shorten(null!, 240));
    }
}
