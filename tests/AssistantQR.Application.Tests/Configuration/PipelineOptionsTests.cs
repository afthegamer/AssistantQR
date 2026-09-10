using AssistantQR.Application.Configuration;

using Xunit;

namespace AssistantQR.Application.Tests.Configuration;

/// <summary>
/// Ces reglages ne sont pas des regles metier, et c'est exactement pour cela qu'ils
/// sont dangereux : ils changent les reponses du systeme sans qu'aucune ligne du
/// Domain ne bouge. <c>Validate</c> existe pour qu'un reglage absurde echoue au
/// demarrage du cas d'usage plutot que de se traduire, dix etapes plus loin, en un
/// refus « aucun document dans le corpus » parfaitement trompeur.
/// </summary>
public sealed class PipelineOptionsTests
{
    [Fact]
    public void Default_IsValid()
    {
        PipelineOptions.Default.Validate();
    }

    [Fact]
    public void Default_UsesPostFilteringAndPermissiveIndexCheck()
    {
        // Les deux defauts du projet sont des choix PEDAGOGIQUES documentes :
        // post-filtrage parce que la purete de la regle prime, verification permissive
        // parce que c'est ce laxisme qui rend la panne silencieuse reproductible.
        Assert.Equal(AccessFilterMode.Post, PipelineOptions.Default.FilterMode);
        Assert.False(PipelineOptions.Default.StrictIndexModelCheck);
        Assert.Equal(0d, PipelineOptions.Default.Temperature);
        Assert.Equal(42, PipelineOptions.Default.Seed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_TopKBelowOne_Throws(int topK)
    {
        var options = PipelineOptions.Default with { TopK = topK };

        var error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("TopK", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    [InlineData(double.NaN)]
    public void Validate_MinScoreOutsideUnitInterval_Throws(double minScore)
    {
        var options = PipelineOptions.Default with { MinScore = minScore };

        var error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("MinScore", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(1d)]
    [InlineData(0.2d)]
    public void Validate_MinScoreOnTheBoundaries_IsAccepted(double minScore)
    {
        (PipelineOptions.Default with { MinScore = minScore }).Validate();
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(double.NaN)]
    public void Validate_NegativeTemperature_Throws(double temperature)
    {
        var options = PipelineOptions.Default with { Temperature = temperature };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Validate_HighTemperature_IsAccepted()
    {
        // Le pipeline n'a pas a decreter qu'une temperature elevee est « mauvaise » :
        // elle est traçable dans l'empreinte de configuration, donc attribuable en cas
        // de derive. C'est la bonne reponse au non-determinisme : le rendre visible,
        // pas l'interdire.
        (PipelineOptions.Default with { Temperature = 1.5 }).Validate();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Validate_MaxTokensBelowOne_Throws(int maxTokens)
    {
        var options = PipelineOptions.Default with { MaxTokens = maxTokens };

        var error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("MaxTokens", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_NullSeed_IsAccepted()
    {
        // Une graine absente est legitime : tous les fournisseurs ne l'acceptent pas.
        // Ce qui compte est que l'absence soit ENREGISTREE dans l'empreinte, pas qu'elle
        // soit interdite.
        (PipelineOptions.Default with { Seed = null }).Validate();
    }

    [Fact]
    public void With_ProducesAnIndependentCopy()
    {
        var modified = PipelineOptions.Default with { TopK = 12 };

        Assert.Equal(12, modified.TopK);
        Assert.Equal(4, PipelineOptions.Default.TopK);
    }
}
