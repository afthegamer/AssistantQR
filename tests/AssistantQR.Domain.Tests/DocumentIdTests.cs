using AssistantQR.Domain.Documents;
using Xunit;

namespace AssistantQR.Domain.Tests;

/// <summary>
/// DocumentId interdit les espaces parce que sa valeur est reprise litteralement dans
/// les citations en ligne « [identifiant] » imposees au modele de langue. Une regle de
/// format ici supprime toute une classe de citations ambigues plus loin dans la chaine.
/// </summary>
public sealed class DocumentIdTests
{
    [Fact]
    public void From_ValeurNormale_ConserveLaValeur()
    {
        var id = DocumentId.From("horaires-ouverture");

        Assert.Equal("horaires-ouverture", id.Value);
        Assert.Equal("horaires-ouverture", id.ToString());
    }

    [Fact]
    public void From_ValeurEntoureeDeBlancs_Trime()
    {
        Assert.Equal("pret-documents", DocumentId.From("\n  pret-documents  ").Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void From_ValeurVideOuBlanche_LeveDomainException(string value)
    {
        Assert.Throws<DomainException>(() => DocumentId.From(value));
    }

    [Fact]
    public void From_ValeurNulle_LeveDomainException()
    {
        Assert.Throws<DomainException>(() => DocumentId.From(null!));
    }

    [Theory]
    [InlineData("horaires ouverture")]
    [InlineData("a b")]
    [InlineData("tab\tinterne")]
    [InlineData("saut\nligne")]
    public void From_ValeurContenantUnBlancInterieur_LeveDomainException(string value)
    {
        Assert.Throws<DomainException>(() => DocumentId.From(value));
    }

    [Fact]
    public void From_CentVingtHuitCaracteres_EstAccepte()
    {
        var limite = new string('d', 128);

        Assert.Equal(limite, DocumentId.From(limite).Value);
    }

    [Fact]
    public void From_PlusDeCentVingtHuitCaracteres_LeveDomainException()
    {
        Assert.Throws<DomainException>(() => DocumentId.From(new string('d', 129)));
    }

    [Fact]
    public void Egalite_MemeValeur_MemesIdentifiants()
    {
        Assert.Equal(DocumentId.From("budget-acquisitions"), DocumentId.From(" budget-acquisitions "));
        Assert.NotEqual(DocumentId.From("budget-acquisitions"), DocumentId.From("planning-agents"));
    }

    [Fact]
    public void Default_StructNonInitialisee_RendUneChaineVideEtPasNull()
    {
        DocumentId implicite = default;

        Assert.Equal(string.Empty, implicite.Value);
    }
}
