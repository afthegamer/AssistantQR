using AssistantQR.Application.Model;
using AssistantQR.Domain.Access;
using AssistantQR.Domain.Documents;
using AssistantQR.Infrastructure.Embeddings;
using AssistantQR.Infrastructure.VectorIndex;

using Xunit;
using Xunit.Abstractions;

namespace AssistantQR.Infrastructure.Tests.Integration;

/// <summary>
/// Tests des adaptateurs HTTP contre le service Python reel.
/// </summary>
/// <remarks>
/// LE TEST QUI COMPTE EST LE DERNIER. Il verifie que <c>/index/search</c> accepte SANS
/// BRONCHER un vecteur issu d'un autre modele que celui qui a construit l'index, tant
/// que la dimension correspond. Ce n'est pas une lacune du service : c'est le contrat,
/// et c'est la panne silencieuse que le cours veut montrer. Un index qui refuserait la
/// requete rendrait la demonstration impossible ; la detection appartient au C#, a
/// partir de <c>/index/metadata</c>.
///
/// Ce fichier est aussi le seul endroit ou l'on verifie que les deux cotes du contrat
/// JSON s'accordent reellement : le <c>snake_case</c>, la circulation des niveaux
/// d'acces en anglais, la forme de <c>built_at</c>. Un contrat ecrit dans un fichier
/// Markdown n'engage personne tant que rien ne l'execute.
/// </remarks>
[Trait("Category", IntegrationTestBase.CategoryName)]
public sealed class PythonServiceIntegrationTests : IntegrationTestBase
{
    private const string ChunkingStrategyId = "paragraph";

    public PythonServiceIntegrationTests(ITestOutputHelper output)
        : base(output)
    {
    }

    [Fact]
    public async Task EmbedQueryAsync_ServiceReel_RendUnVecteurDeLaDimensionAnnoncee()
    {
        if (ShouldSkip())
        {
            return;
        }

        using var client = CreateClient(EmbeddingServiceUrl, timeoutSeconds: 120);
        var descriptor = await ReadModelDescriptorAsync(client);
        var service = new HttpEmbeddingService(client, descriptor);

        var vector = await service.EmbedQueryAsync("Quels sont les horaires d'ouverture le samedi ?");

        Assert.Equal(descriptor.Dimension, vector.Dimension);
    }

    [Fact]
    public async Task EmbedDocumentsAsync_ServiceReel_RendUnVecteurParTexte()
    {
        if (ShouldSkip())
        {
            return;
        }

        using var client = CreateClient(EmbeddingServiceUrl, timeoutSeconds: 120);
        var descriptor = await ReadModelDescriptorAsync(client);
        var service = new HttpEmbeddingService(client, descriptor);

        var vectors = await service.EmbedDocumentsAsync(new[]
        {
            "La médiathèque ouvre du mardi au samedi.",
            "Le prêt court sur trois semaines.",
        });

        Assert.Equal(2, vectors.Count);
        Assert.All(vectors, vector => Assert.Equal(descriptor.Dimension, vector.Dimension));
    }

    [Fact]
    public async Task IndexVectoriel_AllerRetourComplet_RendLeMorceauInsere()
    {
        if (ShouldSkip())
        {
            return;
        }

        using var client = CreateClient(EmbeddingServiceUrl, timeoutSeconds: 120);
        var descriptor = await ReadModelDescriptorAsync(client);

        var embeddings = new HttpEmbeddingService(client, descriptor);
        var index = new HttpVectorIndex(client);

        await index.ResetAsync(descriptor, ChunkingStrategyId);

        var text = "La médiathèque ouvre du mardi au samedi de dix heures à dix-neuf heures.";
        var vectors = await embeddings.EmbedDocumentsAsync(new[] { text });

        var documentId = DocumentId.From("horaires-ouverture");
        var chunk = new Chunk(
            Chunk.BuildId(documentId, 0),
            documentId,
            "Horaires d'ouverture au public",
            AccessLevel.Public,
            0,
            text);

        await index.UpsertAsync(new[] { new IndexedChunk(chunk, vectors[0]) });

        var query = await embeddings.EmbedQueryAsync("Quels sont les horaires du samedi ?");
        var results = await index.SearchAsync(query, topK: 3, SearchFilter.NoFilter);

        Assert.NotEmpty(results);
        Assert.Equal("horaires-ouverture", results[0].Fragment.DocumentId.Value);

        var metadata = await index.GetMetadataAsync();

        // Les niveaux d'acces circulent en anglais sur le fil et redeviennent des rangs
        // de ce cote-ci : c'est la traduction a la frontiere, dans l'autre sens.
        Assert.Equal(AccessLevel.Public, results[0].Fragment.AccessLevel);
        Assert.Equal(descriptor.Name, metadata.EmbeddingModel);
        Assert.Equal(ChunkingStrategyId, metadata.ChunkingStrategyId);
        Assert.Equal(1, metadata.ChunkCount);
        Assert.NotNull(metadata.BuiltAt);
    }

    [Fact]
    public async Task SearchAsync_MaxAccessLevel_EstAppliqueParLeServiceReel()
    {
        if (ShouldSkip())
        {
            return;
        }

        using var client = CreateClient(EmbeddingServiceUrl, timeoutSeconds: 120);
        var descriptor = await ReadModelDescriptorAsync(client);

        var embeddings = new HttpEmbeddingService(client, descriptor);
        var index = new HttpVectorIndex(client);

        await index.ResetAsync(descriptor, ChunkingStrategyId);

        var texts = new[]
        {
            "Les retards donnent lieu à une amende de dix centimes par jour et par document.",
            "Le suivi des usagers en contentieux relève du service juridique de la commune.",
        };

        var vectors = await embeddings.EmbedDocumentsAsync(texts);

        await index.UpsertAsync(new[]
        {
            BuildIndexedChunk("retards-amendes", "Retards, relances et amendes", AccessLevel.Public, texts[0], vectors[0]),
            BuildIndexedChunk("contentieux-usagers", "Suivi des usagers en contentieux", AccessLevel.Confidential, texts[1], vectors[1]),
        });

        var query = await embeddings.EmbedQueryAsync("Que se passe-t-il en cas de retard d'un usager ?");
        var results = await index.SearchAsync(query, topK: 10, SearchFilter.UpTo(AccessLevel.Internal));

        Assert.DoesNotContain(results, result => result.Fragment.AccessLevel == AccessLevel.Confidential);
    }

    [Fact]
    public async Task SearchAsync_VecteurDUnAutreModele_EstAccepteSansAvertissement()
    {
        if (ShouldSkip())
        {
            return;
        }

        using var client = CreateClient(EmbeddingServiceUrl, timeoutSeconds: 120);
        var descriptor = await ReadModelDescriptorAsync(client);

        var embeddings = new HttpEmbeddingService(client, descriptor);
        var index = new HttpVectorIndex(client);

        await index.ResetAsync(descriptor, ChunkingStrategyId);

        var text = "La médiathèque ouvre du mardi au samedi de dix heures à dix-neuf heures.";
        var vectors = await embeddings.EmbedDocumentsAsync(new[] { text });

        await index.UpsertAsync(new[]
        {
            BuildIndexedChunk("horaires-ouverture", "Horaires d'ouverture au public", AccessLevel.Public, text, vectors[0]),
        });

        // Un vecteur fabrique par le faux hachage, a la dimension du vrai modele : la
        // dimension concorde, le sens n'a rien a voir. Le service repond quand meme,
        // avec des scores parfaitement plausibles — aucune exception, aucun journal.
        var foreign = await new HashingEmbeddingService(descriptor.Dimension)
            .EmbedQueryAsync("Quels sont les horaires du samedi ?");

        var results = await index.SearchAsync(foreign, topK: 3, SearchFilter.NoFilter);

        Output.WriteLine($"Résultats obtenus avec un vecteur étranger : {results.Count}");

        Assert.NotEmpty(results);
    }

    /// <summary>
    /// Lit <c>/health</c> pour connaitre le modele et la dimension REELLEMENT servis.
    /// Coder ces valeurs en dur ferait echouer le test des qu'on change de modele, ce
    /// qui est precisement l'operation que le depot veut rendre facile.
    /// </summary>
    private static async Task<EmbeddingModelDescriptor> ReadModelDescriptorAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/health");
        response.EnsureSuccessStatusCode();

        using var document = System.Text.Json.JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());

        var root = document.RootElement;

        return new EmbeddingModelDescriptor(
            root.GetProperty("embedding_model").GetString() ?? string.Empty,
            root.GetProperty("dimension").GetInt32());
    }

    private static IndexedChunk BuildIndexedChunk(
        string documentId,
        string title,
        AccessLevel accessLevel,
        string text,
        EmbeddingVector vector)
    {
        var id = DocumentId.From(documentId);

        return new IndexedChunk(
            new Chunk(Chunk.BuildId(id, 0), id, title, accessLevel, 0, text),
            vector);
    }
}
