using AssistantQR.Application.Tests.Doubles;
using AssistantQR.Application.UseCases.IndexCorpus;

using AssistantQR.Domain.Access;
using AssistantQR.Domain.Documents;

using Xunit;

namespace AssistantQR.Application.Tests.UseCases;

/// <summary>
/// L'indexation est la moitie invisible du systeme : personne ne la regarde tant que
/// les reponses semblent correctes. Ces tests portent donc moins sur le resultat que
/// sur les CONDITIONS de sa production — l'ordre des operations et la verification des
/// invariants d'appariement, deux choses dont la violation ne provoque jamais d'erreur
/// visible, seulement des reponses subtilement fausses.
/// </summary>
public sealed class IndexCorpusUseCaseTests
{
    private static Document Doc(string id, string title, AccessLevel level, string content) =>
        new(DocumentId.From(id), title, content, level);

    private static FakeDocumentRepository ThreeDocuments() => new(
        Doc("horaires-ouverture", "Horaires d'ouverture au public", AccessLevel.Public,
            "La mediatheque ouvre du mardi au samedi.\n\nLe samedi, l'ouverture est continue de 10 h a 18 h."),
        Doc("pret-documents", "Regles de pret et de retour", AccessLevel.Public,
            "Le pret courant dure 21 jours.\n\nLes DVD sont pretes pour 7 jours."),
        Doc("planning-agents", "Elaboration du planning des agents", AccessLevel.Internal,
            "Le planning est arrete un mois a l'avance."));

    [Fact]
    public async Task ExecuteAsync_ParagraphChunking_CountsDocumentsAndChunks()
    {
        var index = new FakeVectorIndex();

        var result = await new IndexCorpusUseCase(
            ThreeDocuments(), new ParagraphChunkingDouble(), new FakeEmbeddingService(), index).ExecuteAsync();

        Assert.Equal(3, result.DocumentCount);
        Assert.Equal(5, result.ChunkCount);
        Assert.Equal(5, index.Count);
        Assert.Equal("fake-paragraph", result.ChunkingStrategyId);
        Assert.Equal("fake-embeddings", result.EmbeddingModel);
        Assert.Equal(32, result.Dimension);
    }

    /// <summary>
    /// CE QUI PART A L'ENCODAGE DECIDE DE CE QU'ON RETROUVERA. Ce test fixe le contenu
    /// exact des textes soumis au service d'embeddings — identifiant, titre, puis
    /// morceau. Il ne verifie pas un detail cosmetique : indexer le seul paragraphe rend
    /// un document introuvable par son propre sujet des que ses mots-cles vivent dans le
    /// titre. La panne est totalement silencieuse — l'index se construit, les recherches
    /// repondent, les scores ont l'air normaux, et le bon document n'est simplement
    /// jamais rendu.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_SubmitsDocumentIdentityAlongWithEachChunk()
    {
        var embeddings = new FakeEmbeddingService();

        await new IndexCorpusUseCase(
            ThreeDocuments(), new ParagraphChunkingDouble(), embeddings, new FakeVectorIndex()).ExecuteAsync();

        Assert.All(
            embeddings.SubmittedDocumentTexts,
            text => Assert.Contains('\n', text));

        Assert.Contains(
            embeddings.SubmittedDocumentTexts,
            text => text.StartsWith("planning-agents Elaboration du planning des agents\n", StringComparison.Ordinal));

        // Le texte du morceau doit toujours suivre, sinon on aurait indexe une etiquette
        // et perdu le contenu.
        Assert.Contains(
            embeddings.SubmittedDocumentTexts,
            text => text.Contains("Le planning est arrete un mois a l'avance.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteAsync_WholeDocumentChunking_ProducesOneChunkPerDocument()
    {
        var index = new FakeVectorIndex();

        var result = await new IndexCorpusUseCase(
            ThreeDocuments(), new WholeDocumentChunkingDouble(), new FakeEmbeddingService(), index).ExecuteAsync();

        // Meme corpus, meme modele : seule la strategie a change, et le contenu de
        // l'index n'a plus rien a voir. C'est le principe CACE en une assertion.
        Assert.Equal(3, result.ChunkCount);
    }

    [Fact]
    public async Task ExecuteAsync_ResetsTheIndexBeforeWritingIntoIt()
    {
        var index = new FakeVectorIndex();

        await new IndexCorpusUseCase(
            ThreeDocuments(), new ParagraphChunkingDouble(), new FakeEmbeddingService(), index).ExecuteAsync();

        // L'ORDRE est le test. Ecrire puis remettre a zero viderait l'index sans qu'aucune
        // assertion sur le resultat final ne s'en apercoive : le compte rendu annoncerait
        // fierement cinq morceaux indexes dans un index vide.
        var resetPosition = index.Calls.ToList().IndexOf("Reset");
        var firstUpsertPosition = index.Calls.ToList().FindIndex(call => call.StartsWith("Upsert", StringComparison.Ordinal));

        Assert.True(resetPosition >= 0, "L'index doit avoir ete remis a zero.");
        Assert.True(firstUpsertPosition > resetPosition, "La remise a zero doit preceder la premiere ecriture.");
    }

    [Fact]
    public async Task ExecuteAsync_ResetRecordsTheModelAndTheChunkingStrategy()
    {
        var index = new FakeVectorIndex();

        await new IndexCorpusUseCase(
            ThreeDocuments(), new ParagraphChunkingDouble(), new FakeEmbeddingService("bge-m3", 64), index).ExecuteAsync();

        var metadata = await index.GetMetadataAsync();

        // Sans cette trace, un index est un piege : on peut l'interroger avec les vecteurs
        // d'un autre modele, les dimensions concordent, la recherche repond, et les
        // resultats sont faux. C'est ici que la detection devient possible.
        Assert.Equal("bge-m3", metadata.EmbeddingModel);
        Assert.Equal(64, metadata.Dimension);
        Assert.Equal("fake-paragraph", metadata.ChunkingStrategyId);
        Assert.Equal(5, metadata.ChunkCount);
    }

    [Fact]
    public async Task ExecuteAsync_EmbeddingServiceReturnsFewerVectorsThanTexts_Throws()
    {
        var embeddings = new FakeEmbeddingService { DropVectorsFromEachBatch = 1 };

        var useCase = new IndexCorpusUseCase(
            ThreeDocuments(), new ParagraphChunkingDouble(), embeddings, new FakeVectorIndex());

        // L'appariement morceau/vecteur se fait PAR POSITION. Un decalage d'un seul rang
        // associerait chaque morceau au vecteur du precedent : une corruption totale,
        // parfaitement silencieuse, qui ne se manifesterait que par des reponses
        // legerement hors sujet. On verifie le contrat plutot que de l'esperer.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => useCase.ExecuteAsync());

        Assert.Contains("appariement", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_EmbeddingServiceLiesAboutItsDimension_Throws()
    {
        var embeddings = new FakeEmbeddingService("menteur", 32) { ProducedDimensionOverride = 16 };

        var useCase = new IndexCorpusUseCase(
            ThreeDocuments(), new ParagraphChunkingDouble(), embeddings, new FakeVectorIndex());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => useCase.ExecuteAsync());

        Assert.Contains("menteur", error.Message, StringComparison.Ordinal);
        Assert.Contains("16", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyCorpus_ResetsTheIndexAndReportsZero()
    {
        var index = new FakeVectorIndex();

        var result = await new IndexCorpusUseCase(
            new FakeDocumentRepository(), new ParagraphChunkingDouble(), new FakeEmbeddingService(), index).ExecuteAsync();

        Assert.Equal(0, result.DocumentCount);
        Assert.Equal(0, result.ChunkCount);
        Assert.Contains("Reset", index.Calls);
    }

    [Fact]
    public async Task ExecuteAsync_LargeCorpus_BatchesTheEmbeddingCalls()
    {
        // Le lot n'est pas une optimisation prematuree : un aller-retour HTTP par morceau
        // rend l'indexation d'un corpus reel inutilisable. La taille de lot est fixee a
        // 32 dans le cas d'usage ; 70 morceaux doivent donc donner trois appels.
        var documents = new List<Document>();
        for (var i = 0; i < 70; i++)
        {
            documents.Add(Doc($"document-{i}", $"Document {i}", AccessLevel.Public, $"Contenu du document numero {i}."));
        }

        var embeddings = new FakeEmbeddingService();

        await new IndexCorpusUseCase(
            new FakeDocumentRepository(documents), new WholeDocumentChunkingDouble(), embeddings, new FakeVectorIndex())
            .ExecuteAsync();

        Assert.Equal(3, embeddings.EmbedDocumentsCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_MeasuresEmbeddingTimeSeparatelyFromTotalTime()
    {
        var result = await new IndexCorpusUseCase(
            ThreeDocuments(), new ParagraphChunkingDouble(), new FakeEmbeddingService(), new FakeVectorIndex())
            .ExecuteAsync();

        // Le temps des embeddings domine presque toujours le total en production. L'isoler
        // evite de chercher la lenteur dans le decoupage ou l'ecriture.
        Assert.True(result.EmbeddingDuration <= result.Duration);
    }

    [Fact]
    public void Constructor_NullPort_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new IndexCorpusUseCase(null!, new ParagraphChunkingDouble(), new FakeEmbeddingService(), new FakeVectorIndex()));
        Assert.Throws<ArgumentNullException>(
            () => new IndexCorpusUseCase(new FakeDocumentRepository(), null!, new FakeEmbeddingService(), new FakeVectorIndex()));
    }
}
