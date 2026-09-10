using AssistantQR.Application.Model;
using AssistantQR.Application.Ports;
using AssistantQR.Domain;
using AssistantQR.Domain.Access;
using AssistantQR.Domain.Documents;
using AssistantQR.Domain.Evidence;
using AssistantQR.Infrastructure.Http;

namespace AssistantQR.Infrastructure.VectorIndex;

/// <summary>
/// Adaptateur REEL de <see cref="IVectorIndex"/> : l'index vit dans le service Python,
/// on lui parle en JSON.
/// </summary>
/// <remarks>
/// POINT PEDAGOGIQUE CENTRAL — CE QUE CET ADAPTATEUR REFUSE DE FAIRE.
/// Il ne compare jamais le modele d'embeddings qui a construit l'index avec celui qui
/// produit le vecteur de requete, et il ne refuse jamais une recherche pour ce motif.
/// La tentation est forte : deux lignes suffiraient a comparer <c>GetMetadataAsync()</c>
/// au descripteur du service d'embeddings et a lever une exception.
///
/// On s'en abstient pour deux raisons.
/// 1. Un index vectoriel, dans la vraie vie, ne connait pas le pipeline qui l'utilise :
///    il stocke des nombres et rend des voisins. Lui confier une regle de coherence,
///    c'est lui demander d'arbitrer une question qui n'est pas la sienne.
/// 2. C'est exactement la panne que le cours veut montrer. Tant que la dimension
///    concorde, un vecteur issu d'un autre modele produit des scores parfaitement
///    plausibles et des reponses subtilement fausses — aucune exception, aucun log,
///    juste une degradation silencieuse. La detection se fait en Application, qui lit
///    les metadonnees et emet un avertissement (ou echoue si l'on a demande le mode
///    strict). Deplacer ce controle ici supprimerait la demonstration.
///
/// Ce que l'adaptateur signale, en revanche, ce sont les pannes franches : service
/// injoignable, statut d'erreur, corps illisible, niveau d'acces inconnu.
/// </remarks>
public sealed class HttpVectorIndex : IVectorIndex
{
    private const string ResetPath = "/index/reset";
    private const string UpsertPath = "/index/upsert";
    private const string SearchPath = "/index/search";
    private const string MetadataPath = "/index/metadata";

    private readonly HttpClient _client;
    private readonly JsonHttpContext _context;

    public HttpVectorIndex(HttpClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));

        if (_client.BaseAddress is null)
        {
            throw new VectorIndexException(
                "Le client HTTP de l'index vectoriel n'a pas d'adresse de base. " +
                "Configure AssistantQR:Embeddings:ServiceUrl (par defaut http://localhost:8088).");
        }

        _context = new JsonHttpContext(
            "L'index vectoriel",
            "Demarre le service Python du dossier python/, ou bascule AssistantQR:Profile sur « offline » " +
            "pour utiliser l'index en memoire.",
            (message, inner) => new VectorIndexException(message, inner));
    }

    public async Task ResetAsync(
        EmbeddingModelDescriptor model,
        string chunkingStrategyId,
        CancellationToken cancellationToken = default)
    {
        if (model is null)
        {
            throw new ArgumentNullException(nameof(model));
        }

        var payload = new ResetRequest(model.Name, model.Dimension, chunkingStrategyId ?? string.Empty);

        var response = await JsonHttp
            .PostAsync<ResetRequest, OkResponse>(_client, ResetPath, payload, _context, cancellationToken)
            .ConfigureAwait(false);

        if (!response.Ok)
        {
            throw new VectorIndexException(
                "Le service a repondu 200 mais n'a pas confirme la remise a zero de l'index. " +
                "Relance l'indexation ; si le probleme persiste, redemarre le service Python.");
        }
    }

    public async Task UpsertAsync(IReadOnlyList<IndexedChunk> chunks, CancellationToken cancellationToken = default)
    {
        if (chunks is null)
        {
            throw new ArgumentNullException(nameof(chunks));
        }

        if (chunks.Count == 0)
        {
            return;
        }

        var items = new UpsertItem[chunks.Count];

        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i].Chunk;
            items[i] = new UpsertItem(
                chunk.ChunkId,
                chunk.DocumentId.Value,
                chunk.DocumentTitle,
                // Le niveau d'acces circule en anglais sur le fil : c'est le nom canonique
                // du Domain. La traduction depuis le francais est faite a la lecture du
                // corpus, une fois pour toutes.
                chunk.AccessLevel.Name,
                chunk.Ordinal,
                chunk.Text,
                chunks[i].Vector.ToArray());
        }

        var response = await JsonHttp
            .PostAsync<UpsertRequest, CountResponse>(
                _client, UpsertPath, new UpsertRequest(items), _context, cancellationToken)
            .ConfigureAwait(false);

        if (response.Count != chunks.Count)
        {
            throw new VectorIndexException(
                $"Le service dit avoir enregistre {response.Count} morceau(x) sur {chunks.Count} envoye(s). " +
                "L'index est incomplet : relance une indexation complete du corpus.");
        }
    }

    public async Task<IReadOnlyList<ScoredFragment>> SearchAsync(
        EmbeddingVector query,
        int topK,
        SearchFilter filter,
        CancellationToken cancellationToken = default)
    {
        if (topK < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(topK), topK, "Le nombre de voisins demandes doit valoir au moins 1.");
        }

        if (query.IsEmpty)
        {
            throw new VectorIndexException(
                "Recherche impossible : le vecteur de requete est vide. " +
                "Le service d'embeddings n'a probablement rien renvoye pour cette question.");
        }

        // Aucune verification de coherence modele/index ici : voir la note de classe.
        var payload = new SearchRequest(query.ToArray(), topK, ToMaxAccessLevel(filter));

        var response = await JsonHttp
            .PostAsync<SearchRequest, SearchResponse>(_client, SearchPath, payload, _context, cancellationToken)
            .ConfigureAwait(false);

        return MapResults(response);
    }

    public async Task<IndexMetadata> GetMetadataAsync(CancellationToken cancellationToken = default)
    {
        var response = await JsonHttp
            .GetAsync<MetadataResponse>(_client, MetadataPath, _context, cancellationToken)
            .ConfigureAwait(false);

        // On rend les metadonnees telles quelles, sans jugement. C'est l'Application qui
        // decide si un ecart de modele est un avertissement ou une erreur.
        return new IndexMetadata(
            response.EmbeddingModel ?? string.Empty,
            response.Dimension,
            response.ChunkingStrategyId ?? string.Empty,
            response.ChunkCount,
            response.BuiltAt);
    }

    /// <summary>
    /// Traduit le filtre d'Application en champ du contrat HTTP. <c>null</c> signifie
    /// « aucun pre-filtrage » : c'est le mode post-filtrage, ou l'index voit tout et ou
    /// le tri par habilitation se fait apres coup, en Application.
    /// </summary>
    private static string? ToMaxAccessLevel(SearchFilter filter) => filter switch
    {
        SearchFilter.MaxAccessLevel maximum => maximum.Level.Name,
        _ => null,
    };

    private static IReadOnlyList<ScoredFragment> MapResults(SearchResponse response)
    {
        var results = response.Results;

        if (results is null || results.Count == 0)
        {
            return Array.Empty<ScoredFragment>();
        }

        var mapped = new List<ScoredFragment>(results.Count);

        foreach (var item in results)
        {
            mapped.Add(new ScoredFragment(MapFragment(item), item.Score));
        }

        return mapped;
    }

    private static EvidenceFragment MapFragment(SearchResultItem item)
    {
        try
        {
            return new EvidenceFragment(
                DocumentId.From(item.DocumentId ?? string.Empty),
                item.DocumentTitle ?? string.Empty,
                item.Text ?? string.Empty,
                AccessLevel.Parse(item.AccessLevel ?? string.Empty),
                item.Ordinal);
        }
        catch (DomainException exception)
        {
            // Le Domain vient de refuser une donnee venue du reseau. On retraduit sa
            // plainte en panne d'infrastructure : c'est le service qui a menti sur le
            // format, pas l'utilisateur qui a mal pose sa question.
            throw new VectorIndexException(
                $"L'index a renvoye un resultat inexploitable (morceau « {item.ChunkId} ») : {exception.Message} " +
                "Rappel du contrat : les niveaux d'acces circulent en anglais (public, internal, confidential).",
                exception);
        }
    }

    private sealed record ResetRequest(string Model, int Dimension, string ChunkingStrategyId);

    private sealed record OkResponse(bool Ok);

    private sealed record UpsertRequest(IReadOnlyList<UpsertItem> Items);

    private sealed record UpsertItem(
        string ChunkId,
        string DocumentId,
        string DocumentTitle,
        string AccessLevel,
        int Ordinal,
        string Text,
        float[] Vector);

    private sealed record CountResponse(int Count);

    private sealed record SearchRequest(float[] Vector, int TopK, string? MaxAccessLevel);

    private sealed record SearchResponse(List<SearchResultItem>? Results);

    private sealed record SearchResultItem(
        string? ChunkId,
        string? DocumentId,
        string? DocumentTitle,
        string? AccessLevel,
        int Ordinal,
        string? Text,
        double Score);

    private sealed record MetadataResponse(
        string? EmbeddingModel,
        int Dimension,
        string? ChunkingStrategyId,
        int ChunkCount,
        DateTimeOffset? BuiltAt);
}
