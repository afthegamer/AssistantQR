using AssistantQR.Domain.Access;
using Xunit;

namespace AssistantQR.Domain.Tests;

/// <summary>
/// L'habilitation voyage AVEC la demande. C'est ce choix qui permet a AnswerPolicy de
/// trancher sans interroger le moindre annuaire — donc sans la moindre dependance.
/// Un Requester qui ne porterait qu'un identifiant obligerait le Domain a aller
/// chercher les droits ailleurs, et la purete de la politique serait perdue.
/// </summary>
public sealed class RequesterTests
{
    [Fact]
    public void Anonymous_Toujours_EstIdentifieMaisSansHabilitationParticuliere()
    {
        Assert.Equal(UserId.From("anonymous"), Requester.Anonymous.Id);
        Assert.Equal(AccessLevel.Public, Requester.Anonymous.Clearance);
    }

    [Fact]
    public void Create_IdentifiantEtNiveauValides_ConstruitLeDemandeur()
    {
        var demandeur = Requester.Create("  agent-42 ", "INTERNAL");

        Assert.Equal("agent-42", demandeur.Id.Value);
        Assert.Equal(AccessLevel.Internal, demandeur.Clearance);
    }

    [Theory]
    [InlineData("", "internal")]
    [InlineData("   ", "public")]
    public void Create_IdentifiantInvalide_LeveDomainException(string userId, string clearance)
    {
        Assert.Throws<DomainException>(() => Requester.Create(userId, clearance));
    }

    [Theory]
    [InlineData("agent", "interne")]
    [InlineData("agent", "")]
    [InlineData("agent", "secret-defense")]
    public void Create_NiveauInvalideOuFrancais_LeveDomainException(string userId, string clearance)
    {
        Assert.Throws<DomainException>(() => Requester.Create(userId, clearance));
    }

    /// <summary>Value object : deux demandeurs de memes attributs sont interchangeables.</summary>
    [Fact]
    public void Egalite_MemeIdentifiantEtMemeHabilitation_DemandeursEgaux()
    {
        Assert.Equal(Requester.Create("agent", "internal"), Requester.Create("agent", "internal"));
        Assert.NotEqual(Requester.Create("agent", "internal"), Requester.Create("agent", "confidential"));
        Assert.NotEqual(Requester.Create("agent", "internal"), Requester.Create("autre", "internal"));
    }
}
