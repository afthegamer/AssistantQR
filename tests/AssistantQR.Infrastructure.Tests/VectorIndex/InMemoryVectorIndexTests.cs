using AssistantQR.Application.Model;
using AssistantQR.Domain.Access;
using AssistantQR.Domain.Documents;
using AssistantQR.Infrastructure.Time;
using AssistantQR.Infrastructure.VectorIndex;

using Xunit;

namespace AssistantQR.Infrastructure.Tests.VectorIndex;

/// <summary>
/// Tests de l'index vectoriel en memoire.
/// </summary>
/// <remarks>
/// LES VECTEURS SONT ECRITS A LA MAIN, PAS CALCULES. Passer par un service d'embeddings,
/// meme factice, ferait dependre l'ordre attendu d'un hachage : le test deviendrait
/// illisible et son echec n'apprendrait rien. Ici, la base canonique rend chaque score
/// previsible de tete — un vecteur identique donne 1, un vecteur orthogonal donne 0 —
/// et l'assertion porte donc sur le classement, qui est le sujet.
///
/// Le test du filtre est le plus important : sans lui, la demonstration pre-filtrage
/// contre post-filtrage ne se declencherait jamais hors ligne, et le scenario de fuite
/// d'acces resterait une affirmation du README.
/// </remarks>
public sealed class InMemoryVectorIndexTests
{
    private static readonly EmbeddingModelDescriptor TestModel = new("modele-de-test", 4);

    [Fact]
    public async Task SearchAsync_ApresUpsert_RendLesMorceauxLesPlusProches()
    {
        var index = await BuildPopulatedIndexAsync();

        var results = await index.SearchAsync(Vector(1, 0, 0, 0), topK: 3, SearchFilter.NoFilter);

        Assert.Equal(3, results.Count);
        Assert.Equal("alpha", results[0].Fragment.DocumentId.Value);
        Assert.Equal(1.0, results[0].Score, 1e-6);
    }

    [Fact]
    public async Task SearchAsync_TopK_EstRespecte()
    {
        var index = await BuildPopulatedIndexAsync();

        var results = await index.SearchAsync(Vector(1, 0, 0, 0), topK: 2, SearchFilter.NoFilter);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task SearchAsync_Resultats_SontTriesParScoreDecroissant()
    {
        var index = await BuildPopulatedIndexAsync();

        var results = await index.SearchAsync(Vector(1, 0, 0, 0), topK: 3, SearchFilter.NoFilter);

        var scores = results.Select(result => result.Score).ToArray();

        Assert.Equal(scores.OrderByDescending(score => score).ToArray(), scores);
        Assert.Equal(new[] { "alpha", "gamma", "beta" }, results.Select(r => r.Fragment.DocumentId.Value));
    }

    [Fact]
    public async Task SearchAsync_MaxAccessLevel_EcarteCeQueLeDemandeurNaPasLeDroitDeLire()
    {
        var index = await BuildPopulatedIndexAsync();

        var readableByInternal = await index.SearchAsync(
            Vector(1, 0, 0, 0), topK: 10, SearchFilter.UpTo(AccessLevel.Internal));

        // « gamma » est confidentiel : il ne doit pas sortir de l'index, meme avec le
        // meilleur score. C'est bien une regle metier qui s'invite dans un composant
        // d'infrastructure — le prix a payer pour ne jamais transporter un extrait
        // interdit hors de l'index.
        Assert.DoesNotContain(readableByInternal, result => result.Fragment.DocumentId.Value == "gamma");
        Assert.Equal(2, readableByInternal.Count);
    }

    [Fact]
    public async Task SearchAsync_MaxAccessLevelPublic_NeRendQueLesMorceauxPublics()
    {
        var index = await BuildPopulatedIndexAsync();

        var results = await index.SearchAsync(Vector(1, 0, 0, 0), topK: 10, SearchFilter.UpTo(AccessLevel.Public));

        Assert.Single(results);
        Assert.Equal("alpha", results[0].Fragment.DocumentId.Value);
    }

    [Fact]
    public async Task SearchAsync_SansFiltre_RendAussiLesMorceauxConfidentiels()
    {
        var index = await BuildPopulatedIndexAsync();

        var results = await index.SearchAsync(Vector(1, 0, 0, 0), topK: 10, SearchFilter.NoFilter);

        // C'est ce comportement — l'index rend tout — qui rend le post-filtrage
        // necessaire, et qui rend la fuite possible quand on l'oublie.
        Assert.Contains(results, result => result.Fragment.AccessLevel == AccessLevel.Confidential);
    }

    [Fact]
    public async Task SearchAsync_TopKInferieurAUn_EstRefuse()
    {
        var index = await BuildPopulatedIndexAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => index.SearchAsync(Vector(1, 0, 0, 0), topK: 0, SearchFilter.NoFilter));
    }

    [Fact]
    public async Task GetMetadataAsync_ApresReset_DecritLIndexVideSansDateDeConstruction()
    {
        var index = new InMemoryVectorIndex();

        await index.ResetAsync(TestModel, "paragraph");

        var metadata = await index.GetMetadataAsync();

        Assert.Equal("modele-de-test", metadata.EmbeddingModel);
        Assert.Equal(4, metadata.Dimension);
        Assert.Equal("paragraph", metadata.ChunkingStrategyId);
        Assert.Equal(0, metadata.ChunkCount);

        // Un index vide n'a pas de date de construction : c'est ce qui distingue
        // « jamais indexe » de « indexe puis vide ».
        Assert.Null(metadata.BuiltAt);
    }

    [Fact]
    public async Task GetMetadataAsync_ApresUpsert_CompteLesMorceauxEtDateLaConstruction()
    {
        var moment = new DateTimeOffset(2026, 3, 14, 9, 30, 0, TimeSpan.Zero);
        var index = new InMemoryVectorIndex(new FixedClock(moment));

        await index.ResetAsync(TestModel, "paragraph");
        await index.UpsertAsync(new[] { Indexed("alpha", 0, AccessLevel.Public, 1, 0, 0, 0) });

        var metadata = await index.GetMetadataAsync();

        Assert.Equal(1, metadata.ChunkCount);
        Assert.Equal(moment, metadata.BuiltAt);
    }

    [Fact]
    public async Task ResetAsync_ApresIndexation_VideLIndexEtChangeLeModeleDeclare()
    {
        var index = await BuildPopulatedIndexAsync();

        await index.ResetAsync(new EmbeddingModelDescriptor("autre-modele", 4), "whole-document");

        var metadata = await index.GetMetadataAsync();

        Assert.Equal(0, metadata.ChunkCount);
        Assert.Equal("autre-modele", metadata.EmbeddingModel);
        Assert.Equal("whole-document", metadata.ChunkingStrategyId);
        Assert.Empty(await index.SearchAsync(Vector(1, 0, 0, 0), topK: 5, SearchFilter.NoFilter));
    }

    [Fact]
    public async Task UpsertAsync_MemeIdentifiantDeMorceau_RemplaceLAncienneVersion()
    {
        var index = new InMemoryVectorIndex();
        await index.ResetAsync(TestModel, "paragraph");

        await index.UpsertAsync(new[] { Indexed("alpha", 0, AccessLevel.Public, 1, 0, 0, 0) });
        await index.UpsertAsync(new[] { Indexed("alpha", 0, AccessLevel.Public, 0, 1, 0, 0) });

        var metadata = await index.GetMetadataAsync();
        var results = await index.SearchAsync(Vector(0, 1, 0, 0), topK: 5, SearchFilter.NoFilter);

        Assert.Equal(1, metadata.ChunkCount);
        Assert.Equal(1.0, results[0].Score, 1e-6);
    }

    [Fact]
    public async Task UpsertAsync_DimensionIncoherente_EstRefusee()
    {
        var index = new InMemoryVectorIndex();
        await index.ResetAsync(TestModel, "paragraph");

        var wrongDimension = new IndexedChunk(
            new Chunk("alpha#0", DocumentId.From("alpha"), "Alpha", AccessLevel.Public, 0, "texte"),
            EmbeddingVector.From(new[] { 1f, 0f }));

        // Le controle est a l'ECRITURE seulement : refuser un vecteur de mauvaise taille
        // protege l'integrite de l'index sans rien changer au scenario de la panne
        // silencieuse, qui se joue a la LECTURE, avec un vecteur de bonne dimension mais
        // d'un autre modele.
        await Assert.ThrowsAsync<InvalidOperationException>(() => index.UpsertAsync(new[] { wrongDimension }));
    }

    [Fact]
    public async Task SearchAsync_VecteurDUnAutreModele_NeLeveJamais()
    {
        var index = await BuildPopulatedIndexAsync();

        // La panne silencieuse doit rester reproductible avec le profil « offline » :
        // tant que la dimension concorde, l'index repond sans broncher, avec des scores
        // parfaitement plausibles. La detection se fait en Application.
        var results = await index.SearchAsync(Vector(0.5f, 0.5f, 0.5f, 0.5f), topK: 3, SearchFilter.NoFilter);

        Assert.NotEmpty(results);
    }

    private static async Task<InMemoryVectorIndex> BuildPopulatedIndexAsync()
    {
        var index = new InMemoryVectorIndex();

        await index.ResetAsync(TestModel, "paragraph");
        await index.UpsertAsync(new[]
        {
            Indexed("alpha", 0, AccessLevel.Public, 1f, 0f, 0f, 0f),
            Indexed("beta", 0, AccessLevel.Internal, 0f, 1f, 0f, 0f),
            Indexed("gamma", 0, AccessLevel.Confidential, 0.9f, 0.1f, 0f, 0f),
        });

        return index;
    }

    private static IndexedChunk Indexed(
        string documentId,
        int ordinal,
        AccessLevel accessLevel,
        params float[] values)
    {
        var id = DocumentId.From(documentId);

        var chunk = new Chunk(
            Chunk.BuildId(id, ordinal),
            id,
            $"Document {documentId}",
            accessLevel,
            ordinal,
            $"Texte du morceau {ordinal} du document {documentId}.");

        return new IndexedChunk(chunk, EmbeddingVector.From(values));
    }

    private static EmbeddingVector Vector(params float[] values) => EmbeddingVector.From(values);
}
