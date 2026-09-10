using AssistantQR.Domain.Access;
using AssistantQR.Domain.Documents;
using Xunit;

namespace AssistantQR.Domain.Tests;

/// <summary>
/// Document est la seule ENTITE du Domain : son identite survit a la modification de
/// son contenu. Les tests ci-dessous opposent explicitement ce comportement a celui
/// des value objects voisins — c'est la distinction que le cours cherche a rendre
/// palpable, et elle ne se voit vraiment que dans les tests d'egalite.
/// </summary>
public sealed class DocumentTests
{
    private static Document Build(
        string id = "horaires-ouverture",
        string title = "Horaires d'ouverture au public",
        string content = "La médiathèque ouvre du mardi au samedi.",
        AccessLevel? level = null,
        IReadOnlyList<string>? tags = null) =>
        new(DocumentId.From(id), title, content, level ?? AccessLevel.Public, tags);

    [Fact]
    public void Constructeur_ValeursValides_ExposeLesProprietes()
    {
        var document = Build(level: AccessLevel.Internal, tags: new[] { "accueil" });

        Assert.Equal(DocumentId.From("horaires-ouverture"), document.Id);
        Assert.Equal("Horaires d'ouverture au public", document.Title);
        Assert.Equal("La médiathèque ouvre du mardi au samedi.", document.Content);
        Assert.Equal(AccessLevel.Internal, document.AccessLevel);
        Assert.Equal(new[] { "accueil" }, document.Tags);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructeur_TitreVide_LeveDomainException(string title)
    {
        Assert.Throws<DomainException>(() => Build(title: title));
    }

    [Fact]
    public void Constructeur_TitreNul_LeveDomainException()
    {
        Assert.Throws<DomainException>(() => Build(title: null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  \n ")]
    public void Constructeur_ContenuVide_LeveDomainException(string content)
    {
        Assert.Throws<DomainException>(() => Build(content: content));
    }

    [Fact]
    public void Constructeur_ContenuNul_LeveDomainException()
    {
        Assert.Throws<DomainException>(() => Build(content: null!));
    }

    [Fact]
    public void Constructeur_TitreEtContenuEntoures_LesTrime()
    {
        var document = Build(title: "  Titre  ", content: "\n Contenu \n");

        Assert.Equal("Titre", document.Title);
        Assert.Equal("Contenu", document.Content);
    }

    [Fact]
    public void Tags_NonFournis_RendUneListeVideEtJamaisNull()
    {
        var document = Build(tags: null);

        Assert.NotNull(document.Tags);
        Assert.Empty(document.Tags);
    }

    [Fact]
    public void Tags_AvecBlancsEtEntreesVides_SontTrimesEtLesVidesEcartees()
    {
        var document = Build(tags: new[] { "  accueil ", "", "   ", "horaires", null! });

        Assert.Equal(new[] { "accueil", "horaires" }, document.Tags);
    }

    // --- Egalite par identite : LE point pedagogique du type -----------------

    /// <summary>
    /// Deux instances portant le meme Id mais un titre, un contenu, un niveau et des
    /// etiquettes differents sont le MEME document : on a corrige la fiche, on n'a pas
    /// change de document. C'est exactement ce qu'un value object ne ferait pas.
    /// </summary>
    [Fact]
    public void Equals_MemeIdMaisToutLeResteDifferent_LesDeuxDocumentsSontEgaux()
    {
        var avant = new Document(
            DocumentId.From("retards-amendes"), "Retards et amendes", "Version de 2024",
            AccessLevel.Public, new[] { "pret" });

        var apres = new Document(
            DocumentId.From("retards-amendes"), "Retards, relances et amendes", "Version de 2026",
            AccessLevel.Internal, new[] { "pret", "amende" });

        Assert.Equal(avant, apres);
        Assert.True(avant.Equals(apres));
        Assert.True(avant.Equals((object)apres));
        Assert.Equal(avant.GetHashCode(), apres.GetHashCode());
    }

    [Fact]
    public void Equals_IdsDifferentsMaisContenuIdentique_LesDeuxDocumentsSontDistincts()
    {
        var premier = Build(id: "horaires-ouverture");
        var second = Build(id: "programme-animations");

        Assert.NotEqual(premier, second);
    }

    [Fact]
    public void Equals_ComparaisonAvecNullOuAutreType_RendFaux()
    {
        var document = Build();

        Assert.False(document.Equals(null));
        Assert.False(document.Equals((object?)null));
        Assert.False(document.Equals("horaires-ouverture"));
    }

    /// <summary>L'egalite par identite doit se propager aux collections indexees par hachage.</summary>
    [Fact]
    public void HashSet_DeuxVersionsDuMemeDocument_NEnConserveQuUne()
    {
        var ensemble = new HashSet<Document>
        {
            Build(title: "Version A"),
            Build(title: "Version B"),
        };

        Assert.Single(ensemble);
    }

    // --- Lecture -------------------------------------------------------------

    [Fact]
    public void IsReadableBy_DocumentPublic_LisiblePourTousLesDemandeurs()
    {
        var document = Build(level: AccessLevel.Public);

        Assert.True(document.IsReadableBy(Requester.Anonymous));
        Assert.True(document.IsReadableBy(Requester.Create("agent", "internal")));
        Assert.True(document.IsReadableBy(Requester.Create("direction", "confidential")));
    }

    [Fact]
    public void IsReadableBy_DocumentInterne_IllisiblePourUnDemandeurPublic()
    {
        var document = Build(level: AccessLevel.Internal);

        Assert.False(document.IsReadableBy(Requester.Anonymous));
        Assert.True(document.IsReadableBy(Requester.Create("agent", "internal")));
        Assert.True(document.IsReadableBy(Requester.Create("direction", "confidential")));
    }

    [Fact]
    public void IsReadableBy_DocumentConfidentiel_LisibleUniquementParUnConfidentiel()
    {
        var document = Build(level: AccessLevel.Confidential);

        Assert.False(document.IsReadableBy(Requester.Anonymous));
        Assert.False(document.IsReadableBy(Requester.Create("agent", "internal")));
        Assert.True(document.IsReadableBy(Requester.Create("direction", "confidential")));
    }

    [Fact]
    public void ToString_QuelQueSoitLeDocument_ResumeIdentiteTitreEtNiveau()
    {
        var texte = Build(level: AccessLevel.Internal).ToString();

        Assert.Contains("horaires-ouverture", texte);
        Assert.Contains("Horaires d'ouverture au public", texte);
        Assert.Contains("(internal)", texte);
    }
}
