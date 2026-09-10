using AssistantQR.Application.Model;
using AssistantQR.Infrastructure.Embeddings;

using Xunit;

namespace AssistantQR.Infrastructure.Tests.Embeddings;

/// <summary>
/// Tests de l'embedding factice, deterministe et hors-ligne.
/// </summary>
/// <remarks>
/// CE QUE CES TESTS PROTEGENT REELLEMENT. Une doublure qui renverrait du bruit rendrait
/// les tests verts et vides : on verifierait que la tuyauterie ne casse pas, jamais
/// qu'elle transporte quelque chose. Le test « deux textes proches sont plus proches que
/// deux textes eloignes » est donc le plus important du fichier — c'est lui qui garantit
/// que toute la demonstration hors-ligne, y compris le scenario de fuite d'acces, reste
/// SIGNIFICATIVE et pas seulement executable.
///
/// Le determinisme, lui, n'est pas un detail de confort : c'est ce qui fait d'un
/// instantane une reference. Si l'embedding variait d'une execution a l'autre, la
/// detection de derive signalerait en permanence un changement qui n'en est pas un, et
/// l'outil deviendrait un detecteur de fumee qu'on debranche.
/// </remarks>
public sealed class HashingEmbeddingServiceTests
{
    private const double Tolerance = 1e-5;

    [Fact]
    public async Task EmbedQueryAsync_MemeTexte_RendDeuxFoisLeMemeVecteur()
    {
        var service = new HashingEmbeddingService();

        var first = await service.EmbedQueryAsync("Quels sont les horaires d'ouverture le samedi ?");
        var second = await service.EmbedQueryAsync("Quels sont les horaires d'ouverture le samedi ?");

        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(256)]
    [InlineData(1024)]
    public async Task EmbedQueryAsync_DimensionConfiguree_EstRespectee(int dimension)
    {
        var service = new HashingEmbeddingService(dimension);

        var vector = await service.EmbedQueryAsync("Un texte quelconque à vectoriser.");

        Assert.Equal(dimension, service.Model.Dimension);
        Assert.Equal(dimension, vector.Dimension);
    }

    [Fact]
    public async Task EmbedQueryAsync_TexteNonVide_ProduitUnVecteurDeNormeUn()
    {
        var service = new HashingEmbeddingService();

        var vector = await service.EmbedQueryAsync("La médiathèque municipale des Tilleuls ouvre le samedi.");

        var norm = Math.Sqrt(vector.ToArray().Sum(value => (double)value * value));

        // Sans normalisation L2, un long paragraphe l'emporterait sur une phrase courte
        // par sa seule longueur : le cosinus ne mesurerait plus la ressemblance mais le
        // bavardage.
        Assert.Equal(1.0, norm, Tolerance);
    }

    [Fact]
    public async Task EmbedQueryAsync_TextesProches_SontPlusSimilairesQueDesTextesEloignes()
    {
        var service = new HashingEmbeddingService();

        var question = await service.EmbedQueryAsync("horaires d'ouverture le samedi");
        var near = await service.EmbedQueryAsync("Les horaires d'ouverture au public le samedi matin.");
        var far = await service.EmbedQueryAsync("La grille de rémunération des agents territoriaux.");

        var similarityNear = EmbeddingVector.CosineSimilarity(question, near);
        var similarityFar = EmbeddingVector.CosineSimilarity(question, far);

        Assert.True(
            similarityNear > similarityFar,
            $"Le texte proche devrait l'emporter : {similarityNear:F4} contre {similarityFar:F4}.");
        Assert.True(similarityNear > 0.3, $"La similarité lexicale attendue est nette : {similarityNear:F4}.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmbedQueryAsync_TexteVide_RendUnVecteurNulDeLaBonneDimension(string text)
    {
        var service = new HashingEmbeddingService(64);

        var vector = await service.EmbedQueryAsync(text);

        // Un vecteur nul est la reponse honnete (« aucune direction »), la ou une
        // exception ferait tomber toute une indexation pour un paragraphe blanc.
        Assert.Equal(64, vector.Dimension);
        Assert.All(vector.ToArray(), value => Assert.Equal(0f, value));
        Assert.Equal(0.0, EmbeddingVector.CosineSimilarity(vector, vector));
    }

    [Theory]
    [InlineData("Médiathèque", "mediatheque")]
    [InlineData("RETARDS ET AMENDES", "retards et amendes")]
    [InlineData("Désherbage des collections", "desherbage des collections")]
    public async Task EmbedQueryAsync_AccentsEtCasse_NeChangentPasLeVecteur(string left, string right)
    {
        var service = new HashingEmbeddingService();

        var first = await service.EmbedQueryAsync(left);
        var second = await service.EmbedQueryAsync(right);

        // « mediatheque » et « médiathèque » designent la meme chose pour un humain,
        // mais pas pour un hachage : le repliement du texte est ce qui les reconcilie.
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task EmbedDocumentsAsync_LotDeTextes_RendUnVecteurParTexteDansLOrdre()
    {
        var service = new HashingEmbeddingService(128);

        var texts = new[]
        {
            "Le prêt court sur trois semaines.",
            "Les amendes sont plafonnées à dix euros.",
            "Le désherbage a lieu chaque été.",
        };

        var vectors = await service.EmbedDocumentsAsync(texts);

        Assert.Equal(texts.Length, vectors.Count);
        Assert.All(vectors, vector => Assert.Equal(128, vector.Dimension));

        for (var i = 0; i < texts.Length; i++)
        {
            Assert.Equal(await service.EmbedQueryAsync(texts[i]), vectors[i]);
        }
    }

    [Fact]
    public void Model_NomParDefaut_EstCeluiDuFauxEtNonCeluiQuiEstConfigure()
    {
        var service = new HashingEmbeddingService();

        // Le faux garde son propre nom : ce nom part dans les metadonnees de l'index, et
        // un faux qui se ferait passer pour « qwen3-embedding:0.6b » rendrait
        // indetectable l'incoherence modele/index que le scenario B cherche a montrer.
        Assert.Equal("hashing-fake", service.Model.Name);
    }

    [Fact]
    public async Task EmbedQueryAsync_DeuxGrainesDeMemeDimension_ProduisentDesVecteursDifferents()
    {
        const string text = "Quels sont les horaires d'ouverture le samedi ?";

        var first = new HashingEmbeddingService(1024, "hashing-fake");
        var second = new HashingEmbeddingService(1024, "hashing-fake-b", 0x9E3779B1);

        var left = await first.EmbedQueryAsync(text);
        var right = await second.EmbedQueryAsync(text);

        // C'EST LE TEST QUI REND LE SCENARIO B DEMONTRABLE HORS LIGNE. Sans graine, deux
        // faux de meme dimension rendraient le meme vecteur, la derive serait nulle, et la
        // demonstration devrait faire varier la DIMENSION — ce qui fait tomber l'index avec
        // une erreur franche et montre exactement l'inverse de la panne silencieuse.
        Assert.Equal(left.Dimension, right.Dimension);
        Assert.NotEqual(left, right);
        Assert.True(
            EmbeddingVector.CosineSimilarity(left, right) < 0.5,
            "Deux projections independantes ne doivent pas se ressembler par accident.");
    }

    [Fact]
    public async Task EmbedQueryAsync_GraineParDefaut_ReproduitLeComportementHistorique()
    {
        var implicite = new HashingEmbeddingService(512);
        var explicite = new HashingEmbeddingService(512, "hashing-fake", HashingEmbeddingService.DefaultSeed);

        var left = await implicite.EmbedQueryAsync("Les amendes sont plafonnées à dix euros.");
        var right = await explicite.EmbedQueryAsync("Les amendes sont plafonnées à dix euros.");

        // La graine par defaut est l'offset canonique de FNV-1a : les instantanes deja
        // enregistres restent comparables a ceux de la semaine prochaine.
        Assert.Equal(HashingEmbeddingService.DefaultSeed, implicite.Seed);
        Assert.Equal(left, right);
    }

    [Theory]
    [InlineData(HashingEmbeddingService.DefaultSeed)]
    [InlineData(0x9E3779B1u)]
    [InlineData(1u)]
    public async Task EmbedQueryAsync_QuelleQueSoitLaGraine_ResteDeterministeNormeEtSignificatif(uint seed)
    {
        var service = new HashingEmbeddingService(1024, "hashing-fake-variante", seed);

        var question = await service.EmbedQueryAsync("horaires d'ouverture le samedi");
        var again = await service.EmbedQueryAsync("horaires d'ouverture le samedi");
        var near = await service.EmbedQueryAsync("Les horaires d'ouverture au public le samedi matin.");
        var far = await service.EmbedQueryAsync("La grille de rémunération des agents territoriaux.");

        // Changer de graine change la projection, jamais les proprietes qui font de ce
        // faux une doublure utile : deterministe, normee, et lexicalement significative.
        Assert.Equal(question, again);
        Assert.Equal(1.0, Math.Sqrt(question.ToArray().Sum(value => (double)value * value)), Tolerance);
        Assert.True(
            EmbeddingVector.CosineSimilarity(question, near) > EmbeddingVector.CosineSimilarity(question, far),
            "Le texte proche doit rester plus proche, quelle que soit la graine.");
    }

    [Theory]
    [InlineData("Médiathèque", "mediatheque")]
    [InlineData("RETARDS ET AMENDES", "retards et amendes")]
    public async Task EmbedQueryAsync_AutreGraine_ReplieToujoursLesAccentsEtLaCasse(string left, string right)
    {
        var service = new HashingEmbeddingService(256, "hashing-fake-b", 0x9E3779B1);

        Assert.Equal(await service.EmbedQueryAsync(left), await service.EmbedQueryAsync(right));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructeur_DimensionInvalide_EstRefusee(int dimension)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HashingEmbeddingService(dimension));
    }

    [Fact]
    public void Constructeur_NomVide_EstRefuse()
    {
        Assert.Throws<ArgumentException>(() => new HashingEmbeddingService(256, "   "));
    }
}
