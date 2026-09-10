using AssistantQR.Application.Model;

using AssistantQR.Domain.Access;

using Xunit;

namespace AssistantQR.Application.Tests.Model;

/// <summary>
/// <c>SearchFilter</c> est le type qui materialise le dilemme du filtrage : c'est par
/// lui qu'une regle metier — le controle d'acces — traverse la frontiere et entre dans
/// le contrat d'un composant technique. Sa hierarchie est FERMEE (constructeur prive,
/// deux cas et pas un de plus) pour que ce passage reste enumerable : on peut lire les
/// deux seules facons dont l'index apprend quelque chose du metier.
/// </summary>
public sealed class SearchFilterTests
{
    [Fact]
    public void NoFilter_IsTheNoneCase()
    {
        Assert.IsType<SearchFilter.None>(SearchFilter.NoFilter);
    }

    [Fact]
    public void NoFilter_IsASharedInstance()
    {
        Assert.Same(SearchFilter.NoFilter, SearchFilter.NoFilter);
    }

    [Fact]
    public void UpTo_CarriesTheRequestedCeiling()
    {
        var filter = Assert.IsType<SearchFilter.MaxAccessLevel>(SearchFilter.UpTo(AccessLevel.Internal));

        Assert.Equal(AccessLevel.Internal, filter.Level);
    }

    [Fact]
    public void Equality_IsByValue_SoATestCanAssertWhatTheStrategySent()
    {
        // Cette egalite structurelle est ce qui rend verifiable l'affirmation
        // « le pre-filtrage envoie l'habilitation a l'index, le post-filtrage non ».
        Assert.Equal(SearchFilter.UpTo(AccessLevel.Internal), SearchFilter.UpTo(AccessLevel.Internal));
        Assert.NotEqual(SearchFilter.UpTo(AccessLevel.Internal), SearchFilter.UpTo(AccessLevel.Public));
        Assert.NotEqual(SearchFilter.NoFilter, SearchFilter.UpTo(AccessLevel.Public));
    }

    [Fact]
    public void UpTo_Public_IsNotTheSameThingAsNoFilter()
    {
        // Nuance qui a coute cher a d'autres : « pas de filtre » n'est pas « filtre au
        // niveau le plus bas ». Le premier laisse remonter du confidentiel, le second
        // non. Les confondre serait une faille, pas une optimisation.
        Assert.NotEqual(SearchFilter.NoFilter, SearchFilter.UpTo(AccessLevel.Public));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void UpTo_AcceptsEveryDomainLevel(int rank)
    {
        var level = AccessLevel.FromRank(rank);

        var filter = Assert.IsType<SearchFilter.MaxAccessLevel>(SearchFilter.UpTo(level));

        Assert.Equal(level, filter.Level);
    }
}
