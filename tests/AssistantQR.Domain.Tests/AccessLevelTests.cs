using AssistantQR.Domain;
using AssistantQR.Domain.Access;
using Xunit;

namespace AssistantQR.Domain.Tests;

/// <summary>
/// AccessLevel est un ordre total, pas une etiquette. Ces tests verrouillent l'ordre
/// lui-meme : tout le controle d'acces du systeme se reduit a une comparaison de rangs,
/// donc une erreur d'un seul cran ici se traduirait par une fuite de document.
/// </summary>
public sealed class AccessLevelTests
{
    [Fact]
    public void Rank_TroisNiveaux_OrdreCroissant()
    {
        Assert.Equal(0, AccessLevel.Public.Rank);
        Assert.Equal(1, AccessLevel.Internal.Rank);
        Assert.Equal(2, AccessLevel.Confidential.Rank);
    }

    [Fact]
    public void Name_TroisNiveaux_NomsAnglaisCanoniques()
    {
        Assert.Equal("public", AccessLevel.Public.Name);
        Assert.Equal("internal", AccessLevel.Internal.Name);
        Assert.Equal("confidential", AccessLevel.Confidential.Name);
    }

    [Fact]
    public void ToString_QuelQueSoitLeNiveau_RendLeNom()
    {
        Assert.Equal("public", AccessLevel.Public.ToString());
        Assert.Equal("internal", AccessLevel.Internal.ToString());
        Assert.Equal("confidential", AccessLevel.Confidential.ToString());
    }

    [Fact]
    public void All_Toujours_ContientLesTroisNiveauxDansLOrdreCroissant()
    {
        Assert.Equal(3, AccessLevel.All.Count);
        Assert.Equal(AccessLevel.Public, AccessLevel.All[0]);
        Assert.Equal(AccessLevel.Internal, AccessLevel.All[1]);
        Assert.Equal(AccessLevel.Confidential, AccessLevel.All[2]);
    }

    /// <summary>
    /// Le defaut d'une struct n'est pas negociable : c'est ce que l'on obtient d'un
    /// champ non initialise, d'un tableau fraichement alloue ou d'une deserialisation
    /// bancale. Ici, ce defaut doit etre le niveau le PLUS ferme cote lecture, donc
    /// « public » : un document dont on aurait perdu le niveau reste lisible par tous,
    /// mais une habilitation perdue ne donne acces a rien de plus que le public.
    /// </summary>
    [Fact]
    public void Default_StructNonInitialisee_VautPublic()
    {
        AccessLevel implicite = default;

        Assert.Equal(AccessLevel.Public, implicite);
        Assert.Equal(0, implicite.Rank);
        Assert.Equal("public", implicite.Name);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void FromRank_RangValide_RendLeNiveauCorrespondant(int rank)
    {
        var level = AccessLevel.FromRank(rank);

        Assert.Equal(rank, level.Rank);
        Assert.Equal(AccessLevel.All[rank], level);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void FromRank_RangHorsBornes_LeveDomainException(int rank)
    {
        Assert.Throws<DomainException>(() => AccessLevel.FromRank(rank));
    }

    [Theory]
    [InlineData("public", 0)]
    [InlineData("internal", 1)]
    [InlineData("confidential", 2)]
    [InlineData("PUBLIC", 0)]
    [InlineData("Internal", 1)]
    [InlineData("  confidential  ", 2)]
    public void Parse_NomAnglaisEventuellementDecore_RendLeNiveau(string value, int expectedRank)
    {
        Assert.Equal(AccessLevel.FromRank(expectedRank), AccessLevel.Parse(value));
    }

    /// <summary>
    /// Le francais n'entre pas dans le Domain. « interne » est un mot du corpus, donc
    /// une affaire de l'Infrastructure qui lit les fichiers ; le Domain ne connait que
    /// son vocabulaire canonique. Ce test protege cette frontiere.
    /// </summary>
    [Theory]
    [InlineData("interne")]
    [InlineData("confidentiel")]
    [InlineData("prive")]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_NomInconnuOuFrancais_LeveDomainException(string value)
    {
        Assert.Throws<DomainException>(() => AccessLevel.Parse(value));
    }

    [Fact]
    public void TryParse_NomConnu_RendVraiEtLeNiveau()
    {
        Assert.True(AccessLevel.TryParse("INTERNAL", out var level));
        Assert.Equal(AccessLevel.Internal, level);
    }

    [Fact]
    public void TryParse_NomInconnu_RendFauxEtLeNiveauParDefaut()
    {
        Assert.False(AccessLevel.TryParse("interne", out var level));
        Assert.Equal(AccessLevel.Public, level);
    }

    [Fact]
    public void TryParse_Null_RendFauxSansLever()
    {
        Assert.False(AccessLevel.TryParse(null, out var level));
        Assert.Equal(AccessLevel.Public, level);
    }

    // --- Les 9 combinaisons de IsReadableWith -------------------------------
    // Un document de niveau N est lisible par une habilitation H si et seulement
    // si N <= H. Les neuf cas sont enumeres explicitement : c'est la table de
    // verite du controle d'acces, elle merite d'etre lisible d'un coup d'oeil.

    [Fact]
    public void IsReadableWith_DocumentPublic_LisiblePourLesTroisHabilitations()
    {
        Assert.True(AccessLevel.Public.IsReadableWith(AccessLevel.Public));
        Assert.True(AccessLevel.Public.IsReadableWith(AccessLevel.Internal));
        Assert.True(AccessLevel.Public.IsReadableWith(AccessLevel.Confidential));
    }

    [Fact]
    public void IsReadableWith_DocumentInterne_LisibleSeulementAPartirDInterne()
    {
        Assert.False(AccessLevel.Internal.IsReadableWith(AccessLevel.Public));
        Assert.True(AccessLevel.Internal.IsReadableWith(AccessLevel.Internal));
        Assert.True(AccessLevel.Internal.IsReadableWith(AccessLevel.Confidential));
    }

    [Fact]
    public void IsReadableWith_DocumentConfidentiel_LisibleSeulementParConfidentiel()
    {
        Assert.False(AccessLevel.Confidential.IsReadableWith(AccessLevel.Public));
        Assert.False(AccessLevel.Confidential.IsReadableWith(AccessLevel.Internal));
        Assert.True(AccessLevel.Confidential.IsReadableWith(AccessLevel.Confidential));
    }

    [Fact]
    public void CompareTo_DeuxNiveaux_SuitLOrdreDesRangs()
    {
        Assert.True(AccessLevel.Public.CompareTo(AccessLevel.Internal) < 0);
        Assert.True(AccessLevel.Confidential.CompareTo(AccessLevel.Internal) > 0);
        Assert.Equal(0, AccessLevel.Internal.CompareTo(AccessLevel.Internal));
    }

    [Fact]
    public void Operateurs_DeuxNiveaux_SuiventLOrdreDesRangs()
    {
        Assert.True(AccessLevel.Public < AccessLevel.Internal);
        Assert.True(AccessLevel.Internal < AccessLevel.Confidential);
        Assert.True(AccessLevel.Confidential > AccessLevel.Public);
        Assert.True(AccessLevel.Internal <= AccessLevel.Internal);
        Assert.True(AccessLevel.Internal >= AccessLevel.Internal);
        Assert.False(AccessLevel.Confidential <= AccessLevel.Internal);
    }

    /// <summary>Value object : deux niveaux de meme rang sont indiscernables.</summary>
    [Fact]
    public void Egalite_MemeRang_MemesValeurs()
    {
        var reconstruit = AccessLevel.FromRank(1);

        Assert.Equal(AccessLevel.Internal, reconstruit);
        Assert.True(AccessLevel.Internal == reconstruit);
        Assert.Equal(AccessLevel.Internal.GetHashCode(), reconstruit.GetHashCode());
        Assert.NotEqual(AccessLevel.Internal, AccessLevel.Confidential);
    }

    [Fact]
    public void Tri_ListeDeNiveaux_UtiliseIComparable()
    {
        var desordre = new List<AccessLevel>
        {
            AccessLevel.Confidential, AccessLevel.Public, AccessLevel.Internal,
        };

        desordre.Sort();

        Assert.Equal(AccessLevel.All, desordre);
    }
}
