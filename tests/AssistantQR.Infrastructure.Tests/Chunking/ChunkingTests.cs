using System.Text;

using AssistantQR.Application.Ports;
using AssistantQR.Domain.Access;
using AssistantQR.Domain.Documents;
using AssistantQR.Infrastructure.Chunking;

using Xunit;

namespace AssistantQR.Infrastructure.Tests.Chunking;

/// <summary>
/// Tests des trois strategies de decoupage et de leur fabrique.
/// </summary>
/// <remarks>
/// CES TESTS SONT LA CONTRE-MESURE AU PRINCIPE CACE, PAS SON ANTIDOTE. Changer la
/// strategie de decoupage change toutes les reponses du systeme sans casser la moindre
/// compilation ; aucun test ne peut l'empecher. Ce que ceux-ci garantissent est plus
/// modeste et plus utile : que chaque strategie fait bien ce que son identifiant
/// annonce, et que cet identifiant — qui finit dans les metadonnees de l'index et dans
/// l'empreinte des instantanes — decrit fidelement le reglage employe. Une derive
/// attribuable vaut mieux qu'une derive interdite.
/// </remarks>
public sealed class ChunkingTests
{
    [Fact]
    public void Split_Paragraph_DecoupeSurLesLignesVides()
    {
        var document = BuildDocument(
            "pret-documents",
            string.Join("\n\n",
                "Chaque abonné peut emprunter jusqu'à dix documents pour une durée de trois semaines pleines.",
                "La prolongation se demande en ligne depuis le compte lecteur, une seule fois par document.",
                "Les nouveautés et les documents réservés par un autre usager sont exclus de la prolongation."));

        var chunks = new ParagraphChunkingStrategy(minChars: 60).Split(document);

        Assert.Equal(3, chunks.Count);
        Assert.All(chunks, chunk => Assert.Equal("pret-documents", chunk.DocumentId.Value));
    }

    [Fact]
    public void Split_Paragraph_FusionneLesParagraphesTropCourtsAvecLeSuivant()
    {
        var document = BuildDocument(
            "inscription-abonnement",
            string.Join("\n\n",
                "Tarif réduit : 4 €.",
                "Les abonnés de moins de dix-huit ans bénéficient de la gratuité totale sur l'ensemble des collections.",
                "Le prêt court sur trois semaines et peut être prolongé une seule fois depuis le compte lecteur."));

        var chunks = new ParagraphChunkingStrategy(minChars: 50).Split(document);

        // Un morceau de vingt caracteres produit un vecteur domine par deux mots : il
        // remonte tres haut sur une requete qui les contient, et n'appuie jamais rien.
        // La fusion avec le paragraphe suivant lui rend le contexte qui lui manquait.
        Assert.Equal(2, chunks.Count);
        Assert.Contains("Tarif réduit", chunks[0].Text, StringComparison.Ordinal);
        Assert.Contains("moins de dix-huit ans", chunks[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Tarif réduit", chunks[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Split_Paragraph_IgnoreLesLignesDeTitreSeules()
    {
        var document = BuildDocument(
            "programme-animations",
            string.Join("\n\n",
                "## Tarifs",
                "L'entrée aux animations culturelles est gratuite pour tous les publics, sur inscription préalable."));

        var chunks = new ParagraphChunkingStrategy(minChars: 40).Split(document);

        // Un bloc reduit a un titre markdown n'apporte aucun contenu citable.
        Assert.Single(chunks);
        Assert.DoesNotContain("## Tarifs", chunks[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Split_Paragraph_DocumentSansProse_ProduitQuandMemeUnMorceau()
    {
        var document = BuildDocument("titre-seul", "## Un titre et rien d'autre");

        var chunks = new ParagraphChunkingStrategy().Split(document);

        // Filet de securite : un document qui disparait silencieusement de l'index est
        // un trou qui se diagnostique tres mal. Mieux vaut un morceau imparfait.
        Assert.Single(chunks);
        Assert.Equal("titre-seul#0", chunks[0].ChunkId);
    }

    [Fact]
    public void Split_FixedSize_ProduitUnRecouvrementEntreMorceauxConsecutifs()
    {
        // Un texte de mots numerotes rend le recouvrement lisible a l'oeil : le premier
        // mot du second morceau doit encore figurer dans le premier.
        var builder = new StringBuilder();
        for (var i = 0; i < 50; i++)
        {
            if (i > 0)
            {
                builder.Append(' ');
            }

            builder.Append('w').Append(i.ToString("000"));
        }

        var document = BuildDocument("texte-regulier", builder.ToString());

        var chunks = new FixedSizeChunkingStrategy(size: 60, overlap: 20).Split(document);

        Assert.True(chunks.Count >= 2, "Le texte doit produire plusieurs morceaux.");

        var firstWordOfSecondChunk = chunks[1].Text.Split(' ')[0];
        Assert.Contains(firstWordOfSecondChunk, chunks[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Split_FixedSize_CoupeSurUneFrontiereDeMot()
    {
        var builder = new StringBuilder();
        for (var i = 0; i < 60; i++)
        {
            if (i > 0)
            {
                builder.Append(' ');
            }

            builder.Append("motif").Append(i.ToString("00"));
        }

        var document = BuildDocument("frontiere-de-mot", builder.ToString());

        var chunks = new FixedSizeChunkingStrategy(size: 80, overlap: 10).Split(document);

        // Aucun morceau ne doit se terminer au milieu d'un mot : chaque « motifNN »
        // reste entier, sinon la coupe separe une affirmation de sa condition.
        Assert.All(chunks, chunk => Assert.Matches(@"motif\d\d$", chunk.Text));
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(600, 0)]
    [InlineData(100, 100)]
    [InlineData(100, 200)]
    public void Constructeur_FixedSize_ReglagesIncoherents_LeveArgumentException(int size, int overlap)
    {
        // Un recouvrement superieur ou egal a la taille ne fait pas avancer la fenetre :
        // le decoupage ne terminerait jamais. On refuse a la construction, pas a l'usage.
        Assert.Throws<ArgumentException>(() => new FixedSizeChunkingStrategy(size, overlap));
    }

    [Fact]
    public void Split_WholeDocument_ProduitUnSeulMorceauContenantToutLeTexte()
    {
        var content = string.Join("\n\n",
            "Premier paragraphe du document, suffisamment long pour être indexé seul.",
            "Second paragraphe, tout aussi bavard que le précédent et parfaitement inutile.");

        var document = BuildDocument("horaires-ouverture", content);

        var chunks = new WholeDocumentChunkingStrategy().Split(document);

        Assert.Single(chunks);
        Assert.Equal("horaires-ouverture#0", chunks[0].ChunkId);
        Assert.Equal(0, chunks[0].Ordinal);
        Assert.Equal(content, chunks[0].Text);
    }

    [Fact]
    public void Split_ToutesStrategies_NumeroteLesMorceauxSelonLaConventionDuContrat()
    {
        var document = BuildDocument(
            "gestion-retards-interne",
            string.Join("\n\n",
                "La relance automatique part le lendemain de la date de retour prévue pour le document.",
                "Une seconde relance est envoyée quinze jours plus tard, avec le montant de l'amende due.",
                "Au-delà de soixante jours, le dossier de l'usager bascule en contentieux interne."));

        var strategies = new IChunkingStrategy[]
        {
            new ParagraphChunkingStrategy(minChars: 40),
            new FixedSizeChunkingStrategy(size: 80, overlap: 20),
            new WholeDocumentChunkingStrategy(),
        };

        foreach (var strategy in strategies)
        {
            var chunks = strategy.Split(document);

            for (var ordinal = 0; ordinal < chunks.Count; ordinal++)
            {
                Assert.Equal(ordinal, chunks[ordinal].Ordinal);
                Assert.Equal($"gestion-retards-interne#{ordinal}", chunks[ordinal].ChunkId);
                Assert.Equal(document.Title, chunks[ordinal].DocumentTitle);
                Assert.Equal(document.AccessLevel, chunks[ordinal].AccessLevel);
            }
        }
    }

    [Theory]
    [InlineData("paragraph", "paragraph")]
    [InlineData("whole-document", "whole-document")]
    [InlineData("fixed", "fixed-600-100")]
    [InlineData("fixed-300-50", "fixed-300-50")]
    [InlineData("  PARAGRAPH  ", "paragraph")]
    public void Create_IdentifiantValide_RendLaStrategieAttendue(string id, string expectedId)
    {
        var strategy = ChunkingStrategyFactory.Create(id);

        // L'identifiant embarque les reglages parce qu'il finit dans les metadonnees de
        // l'index : deux index construits avec des tailles differentes ne doivent
        // surtout pas se ressembler.
        Assert.Equal(expectedId, strategy.Id);
        Assert.False(string.IsNullOrWhiteSpace(strategy.Description));
    }

    [Fact]
    public void Create_IdentifiantInconnu_LeveEnListantLesIdentifiantsValides()
    {
        var exception = Assert.Throws<ArgumentException>(() => ChunkingStrategyFactory.Create("decoupage-magique"));

        Assert.Contains("decoupage-magique", exception.Message, StringComparison.Ordinal);
        Assert.Contains("paragraph", exception.Message, StringComparison.Ordinal);
        Assert.Contains("whole-document", exception.Message, StringComparison.Ordinal);
        Assert.Contains("fixed", exception.Message, StringComparison.Ordinal);
    }

    private static Document BuildDocument(string id, string content) =>
        new(DocumentId.From(id), "Un titre de document", content, AccessLevel.Internal);
}
