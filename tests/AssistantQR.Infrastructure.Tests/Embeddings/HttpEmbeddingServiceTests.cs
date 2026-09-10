using System.Net;
using System.Text;
using System.Text.Json;

using AssistantQR.Application.Model;
using AssistantQR.Infrastructure.Embeddings;
using AssistantQR.Infrastructure.Http;

using Xunit;

namespace AssistantQR.Infrastructure.Tests.Embeddings;

/// <summary>
/// Tests de l'adaptateur HTTP d'embeddings, contre un service simule.
/// </summary>
/// <remarks>
/// CE QUE CES TESTS PROTEGENT REELLEMENT. Le nom du modele d'embeddings vient de la
/// configuration C# ; les vecteurs, eux, viennent du service Python, qui a resolu SON
/// modele au demarrage. Rien, dans le reste du systeme, ne peut constater un desaccord
/// entre les deux : l'etiquette declaree part telle quelle dans les metadonnees de
/// l'index et dans l'empreinte des instantanes, donc tout reste coherent avec soi-meme
/// et l'avertissement d'incoherence index/modele ne se declenche jamais. Les deux tests
/// de refus ci-dessous sont la seule chose qui empeche ce mensonge d'etiquette de
/// revenir un jour par inadvertance.
///
/// Le service est simule par un <see cref="StubHandler"/> qui repond a la fois a
/// <c>/health</c> et a <c>/embed</c> : c'est ce qui permet de dissocier ce que le
/// service ANNONCE de ce qu'on lui a DECLARE, et donc de fabriquer le desaccord
/// exprès — chose impossible contre le vrai service, ou les deux viennent forcement de
/// la meme variable d'environnement.
/// </remarks>
public sealed class HttpEmbeddingServiceTests
{
    private const string RealModel = "qwen3-embedding:0.6b";
    private const int RealDimension = 8;

    [Fact]
    public async Task EmbedQueryAsync_DeclarationConforme_RendLeVecteurDuService()
    {
        using var handler = new StubHandler(RealModel, RealDimension);
        using var client = CreateClient(handler);
        var service = new HttpEmbeddingService(client, new EmbeddingModelDescriptor(RealModel, RealDimension));

        var vector = await service.EmbedQueryAsync("Quels sont les horaires d'ouverture le samedi ?");

        Assert.Equal(RealDimension, vector.Dimension);
        Assert.Equal(1, handler.HealthCallCount);
        Assert.Equal(1, handler.EmbedCallCount);
    }

    [Fact]
    public async Task EmbedDocumentsAsync_DeclarationConforme_RendUnVecteurParTexte()
    {
        using var handler = new StubHandler(RealModel, RealDimension);
        using var client = CreateClient(handler);
        var service = new HttpEmbeddingService(client, new EmbeddingModelDescriptor(RealModel, RealDimension));

        var vectors = await service.EmbedDocumentsAsync(new[]
        {
            "La mediatheque ouvre du mardi au samedi.",
            "Le pret court sur trois semaines.",
        });

        Assert.Equal(2, vectors.Count);
        Assert.All(vectors, vector => Assert.Equal(RealDimension, vector.Dimension));
    }

    [Fact]
    public async Task EmbedAsync_PlusieursAppels_NInterrogeSanteQuUneSeuleFois()
    {
        // La verification est un controle de configuration, pas un battement de coeur :
        // la refaire a chaque lot doublerait les allers-retours pour une information qui
        // ne change pas tant que le service n'a pas redemarre.
        using var handler = new StubHandler(RealModel, RealDimension);
        using var client = CreateClient(handler);
        var service = new HttpEmbeddingService(client, new EmbeddingModelDescriptor(RealModel, RealDimension));

        await service.EmbedQueryAsync("Premiere question.");
        await service.EmbedDocumentsAsync(new[] { "Un document." });
        await service.EmbedQueryAsync("Seconde question.");

        Assert.Equal(1, handler.HealthCallCount);
        Assert.Equal(3, handler.EmbedCallCount);
    }

    [Fact]
    public async Task EmbedQueryAsync_NomDeclareDifferentDuNomServi_Refuse()
    {
        // LE CAS QUI COMPTE : le service tourne sous qwen3, la configuration declare
        // bge-m3. Les dimensions concordent (1024 des deux cotes en vrai), donc AUCUN
        // controle existant ne peut voir le probleme.
        using var handler = new StubHandler(RealModel, RealDimension);
        using var client = CreateClient(handler);
        var service = new HttpEmbeddingService(client, new EmbeddingModelDescriptor("bge-m3", RealDimension));

        var exception = await Assert.ThrowsAsync<EmbeddingServiceException>(
            () => service.EmbedQueryAsync("Quels sont les horaires d'ouverture le samedi ?"));

        // Les deux valeurs doivent figurer : un message qui ne nomme que la bonne ou que
        // la mauvaise laisse l'etudiant deviner laquelle corriger.
        Assert.Contains("bge-m3", exception.Message, StringComparison.Ordinal);
        Assert.Contains(RealModel, exception.Message, StringComparison.Ordinal);

        // Les deux issues concretes.
        Assert.Contains("ASSISTANTQR_EMBEDDINGS__MODEL", exception.Message, StringComparison.Ordinal);
        Assert.Contains("start-embeddings.ps1", exception.Message, StringComparison.Ordinal);

        // Et surtout : aucun vecteur n'a ete calcule. Le refus a lieu AVANT /embed,
        // donc aucune donnee mal etiquetee ne peut atteindre l'index.
        Assert.Equal(0, handler.EmbedCallCount);
    }

    [Fact]
    public async Task EmbedDocumentsAsync_DimensionDeclareeDifferenteDeLaDimensionServie_Refuse()
    {
        using var handler = new StubHandler(RealModel, RealDimension);
        using var client = CreateClient(handler);
        var service = new HttpEmbeddingService(client, new EmbeddingModelDescriptor(RealModel, RealDimension + 1));

        var exception = await Assert.ThrowsAsync<EmbeddingServiceException>(
            () => service.EmbedDocumentsAsync(new[] { "La mediatheque ouvre du mardi au samedi." }));

        Assert.Contains(RealDimension.ToString(), exception.Message, StringComparison.Ordinal);
        Assert.Contains((RealDimension + 1).ToString(), exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, handler.EmbedCallCount);
    }

    [Fact]
    public async Task EmbedQueryAsync_CasseDifferente_EstAccepte()
    {
        // « BGE-M3 » et « bge-m3 » designent le meme modele cote Ollama : ce qui est
        // refuse est un desaccord de modele, pas une difference de frappe.
        using var handler = new StubHandler("bge-m3", RealDimension);
        using var client = CreateClient(handler);
        var service = new HttpEmbeddingService(client, new EmbeddingModelDescriptor("BGE-M3", RealDimension));

        var vector = await service.EmbedQueryAsync("Quels sont les horaires du samedi ?");

        Assert.Equal(RealDimension, vector.Dimension);
    }

    [Fact]
    public async Task EmbedDocumentsAsync_LotVide_NeToucheNiSanteNiCalcul()
    {
        using var handler = new StubHandler(RealModel, RealDimension);
        using var client = CreateClient(handler);
        var service = new HttpEmbeddingService(client, new EmbeddingModelDescriptor(RealModel, RealDimension));

        var vectors = await service.EmbedDocumentsAsync(Array.Empty<string>());

        Assert.Empty(vectors);
        Assert.Equal(0, handler.HealthCallCount);
        Assert.Equal(0, handler.EmbedCallCount);
    }

    private static HttpClient CreateClient(StubHandler handler) =>
        new(handler, disposeHandler: false) { BaseAddress = new Uri("http://localhost:8088") };

    /// <summary>
    /// Service d'embeddings simule : il annonce sur <c>/health</c> le modele qu'on lui
    /// donne a la construction, et sert sur <c>/embed</c> des vecteurs de la dimension
    /// correspondante. Il compte ses appels, parce que « une seule fois » est une
    /// propriete qu'on ne peut verifier qu'en comptant.
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _announcedModel;
        private readonly int _announcedDimension;

        internal StubHandler(string announcedModel, int announcedDimension)
        {
            _announcedModel = announcedModel;
            _announcedDimension = announcedDimension;
        }

        internal int HealthCallCount { get; private set; }

        internal int EmbedCallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (path == "/health")
            {
                HealthCallCount++;
                return Task.FromResult(Json(new
                {
                    status = "ok",
                    embedding_model = _announcedModel,
                    dimension = _announcedDimension,
                    ollama_reachable = true,
                    index_chunk_count = 0,
                }));
            }

            if (path == "/embed")
            {
                EmbedCallCount++;
                var textCount = ReadTextCount(request);
                var vectors = new List<float[]>(textCount);

                for (var i = 0; i < textCount; i++)
                {
                    var values = new float[_announcedDimension];
                    values[i % _announcedDimension] = 1f;
                    vectors.Add(values);
                }

                return Task.FromResult(Json(new
                {
                    model = _announcedModel,
                    dimension = _announcedDimension,
                    vectors,
                }));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
        }

        private static int ReadTextCount(HttpRequestMessage request)
        {
            var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "{}";
            using var document = JsonDocument.Parse(body);

            return document.RootElement.TryGetProperty("texts", out var texts)
                ? texts.GetArrayLength()
                : 0;
        }

        private static HttpResponseMessage Json(object payload) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
    }
}
