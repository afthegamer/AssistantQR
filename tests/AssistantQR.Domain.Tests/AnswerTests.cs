using AssistantQR.Domain.Access;
using AssistantQR.Domain.Answers;
using AssistantQR.Domain.Documents;
using Xunit;

namespace AssistantQR.Domain.Tests;

/// <summary>
/// REGLE METIER 1, verifiee au niveau du TYPE : une réponse sans citation n'existe pas.
/// Ces tests ne verifient pas qu'un validateur refuse une réponse non sourcée ; ils
/// verifient qu'une telle réponse est INCONSTRUCTIBLE. La nuance est tout l'interet
/// de placer l'invariante dans le constructeur plutot que dans une couche superieure :
/// aucun futur developpeur, aucun futur cas d'usage ne pourra la contourner.
/// </summary>
public sealed class AnswerTests
{
    private static Citation Cite(string documentId, int ordinal = 0, string excerpt = "Extrait.") => new(
        DocumentId.From(documentId),
        $"Titre de {documentId}",
        excerpt,
        AccessLevel.Public,
        ordinal);

    [Fact]
    public void Create_TexteEtUneCitation_ConstruitLaReponse()
    {
        var answer = Answer.Create("La médiathèque ouvre à 10 h.", new[] { Cite("horaires-ouverture") });

        Assert.Equal("La médiathèque ouvre à 10 h.", answer.Text);
        Assert.Single(answer.Citations);
        Assert.Equal(DocumentId.From("horaires-ouverture"), answer.Citations[0].DocumentId);
    }

    [Fact]
    public void Create_TexteEntoureDeBlancs_LeTrime()
    {
        var answer = Answer.Create("  Une réponse.  \n", new[] { Cite("horaires-ouverture") });

        Assert.Equal("Une réponse.", answer.Text);
    }

    // --- L'invariante centrale ----------------------------------------------

    [Fact]
    public void Create_SansAucuneCitation_LeveDomainException()
    {
        Assert.Throws<DomainException>(
            () => Answer.Create("Une réponse parfaitement plausible mais non sourcée.", Array.Empty<Citation>()));
    }

    [Fact]
    public void Create_ListeDeCitationsNulle_LeveDomainException()
    {
        Assert.Throws<DomainException>(() => Answer.Create("Une réponse.", null!));
    }

    [Fact]
    public void Create_CitationNulleDansLaListe_LeveDomainException()
    {
        Assert.Throws<DomainException>(
            () => Answer.Create("Une réponse.", new[] { Cite("horaires-ouverture"), null! }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t ")]
    public void Create_TexteVideOuBlanc_LeveDomainException(string text)
    {
        Assert.Throws<DomainException>(() => Answer.Create(text, new[] { Cite("horaires-ouverture") }));
    }

    [Fact]
    public void Create_TexteNul_LeveDomainException()
    {
        Assert.Throws<DomainException>(() => Answer.Create(null!, new[] { Cite("horaires-ouverture") }));
    }

    // --- Doublons -------------------------------------------------------------

    /// <summary>
    /// Citer deux fois le meme fragment n'ajoute aucune source : c'est un defaut de la
    /// réponse, pas une redondance benigne. Le type le refuse.
    /// </summary>
    [Fact]
    public void Create_DeuxCitationsDuMemeDocumentEtDuMemeFragment_LeveDomainException()
    {
        var citations = new[] { Cite("retards-amendes", 2), Cite("retards-amendes", 2, "Autre libellé.") };

        Assert.Throws<DomainException>(() => Answer.Create("Une réponse.", citations));
    }

    /// <summary>
    /// En revanche, deux fragments DIFFERENTS du meme document sont deux sources
    /// distinctes : la réponse s'appuie sur deux passages, elle a le droit.
    /// </summary>
    [Fact]
    public void Create_DeuxFragmentsDistinctsDuMemeDocument_EstAccepte()
    {
        var answer = Answer.Create(
            "Une réponse appuyée sur deux passages.",
            new[] { Cite("retards-amendes", 0), Cite("retards-amendes", 1) });

        Assert.Equal(2, answer.Citations.Count);
        Assert.Single(answer.CitedDocumentIds);
    }

    // --- CitedDocumentIds ------------------------------------------------------

    [Fact]
    public void CitedDocumentIds_PlusieursDocuments_SontDistinctsEtDansLOrdreDApparition()
    {
        var answer = Answer.Create(
            "Une réponse à trois sources.",
            new[]
            {
                Cite("pret-documents", 0),
                Cite("retards-amendes", 0),
                Cite("pret-documents", 1),
                Cite("horaires-ouverture", 0),
            });

        Assert.Equal(
            new[]
            {
                DocumentId.From("pret-documents"),
                DocumentId.From("retards-amendes"),
                DocumentId.From("horaires-ouverture"),
            },
            answer.CitedDocumentIds);
    }

    [Fact]
    public void Citations_ApresConstruction_SontFigeesMemeSiLaListeSourceChange()
    {
        var source = new List<Citation> { Cite("horaires-ouverture") };
        var answer = Answer.Create("Une réponse.", source);

        source.Add(Cite("retards-amendes"));

        Assert.Single(answer.Citations);
        Assert.Single(answer.CitedDocumentIds);
    }
}
