using AssistantQR.Domain.Access;
using Xunit;

namespace AssistantQR.Domain.Tests;

/// <summary>
/// UserId existe pour qu'une chaine vide ne puisse pas circuler jusqu'au controle
/// d'acces sous les traits d'un utilisateur. La validation est payee une seule fois,
/// a la construction ; tout le reste du systeme peut ensuite faire confiance au type.
/// </summary>
public sealed class UserIdTests
{
    [Fact]
    public void From_ValeurNormale_ConserveLaValeur()
    {
        var id = UserId.From("alice");

        Assert.Equal("alice", id.Value);
        Assert.Equal("alice", id.ToString());
    }

    [Fact]
    public void From_ValeurEntoureeDeBlancs_Trime()
    {
        Assert.Equal("alice", UserId.From("   alice \t ").Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void From_ValeurVideOuBlanche_LeveDomainException(string value)
    {
        Assert.Throws<DomainException>(() => UserId.From(value));
    }

    [Fact]
    public void From_ValeurNulle_LeveDomainException()
    {
        Assert.Throws<DomainException>(() => UserId.From(null!));
    }

    [Fact]
    public void From_SoixanteQuatreCaracteres_EstAccepte()
    {
        var limite = new string('a', 64);

        Assert.Equal(limite, UserId.From(limite).Value);
    }

    [Fact]
    public void From_PlusDeSoixanteQuatreCaracteres_LeveDomainException()
    {
        Assert.Throws<DomainException>(() => UserId.From(new string('a', 65)));
    }

    /// <summary>Les blancs sont retires AVANT de mesurer : 64 caracteres utiles restent valides.</summary>
    [Fact]
    public void From_LimiteAtteinteApresTrim_EstAccepte()
    {
        var id = UserId.From("  " + new string('a', 64) + "  ");

        Assert.Equal(64, id.Value.Length);
    }

    [Fact]
    public void Egalite_MemeValeur_MemesIdentifiants()
    {
        Assert.Equal(UserId.From("alice"), UserId.From(" alice "));
        Assert.NotEqual(UserId.From("alice"), UserId.From("Alice"));
    }

    /// <summary>
    /// Piege connu des value objects en struct : <c>default</c> contourne la fabrique.
    /// Le type s'en protege en rendant une chaine vide plutot que <c>null</c>, ce qui
    /// evite une NullReferenceException a distance de son point de creation.
    /// </summary>
    [Fact]
    public void Default_StructNonInitialisee_RendUneChaineVideEtPasNull()
    {
        UserId implicite = default;

        Assert.Equal(string.Empty, implicite.Value);
        Assert.Equal(string.Empty, implicite.ToString());
    }
}
