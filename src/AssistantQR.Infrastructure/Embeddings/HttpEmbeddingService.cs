using AssistantQR.Application.Model;
using AssistantQR.Application.Ports;
using AssistantQR.Infrastructure.Http;

namespace AssistantQR.Infrastructure.Embeddings;

/// <summary>
/// Adaptateur REEL de <see cref="IEmbeddingService"/> : delegue le calcul des vecteurs
/// au service Python, via <c>POST /embed</c>.
/// </summary>
/// <remarks>
/// POURQUOI CETTE CLASSE EST ICI ET PAS AILLEURS : c'est le seul endroit du systeme qui
/// sait qu'un embedding se calcule par un appel HTTP a un processus Python demarre a
/// cote. L'Application, elle, ne connait que « donne-moi un vecteur ». Remplacer le
/// service par une bibliotheque locale, un service maison ou un fournisseur commercial
/// se joue entierement dans ce fichier.
///
/// L'adaptateur ne verifie PAS que le nom de modele annonce par le service correspond a
/// celui qu'on lui a configure. Ce n'est pas un oubli : la detection d'incoherence
/// modele/index se fait en Application, a partir des metadonnees de l'index. Dupliquer
/// ce controle ici en ferait un garde-fou HTTP alors que c'est une decision de pipeline.
/// La dimension, en revanche, est verifiee : un vecteur de mauvaise taille rend toute
/// comparaison mecaniquement impossible.
/// </remarks>
public sealed class HttpEmbeddingService : IEmbeddingService
{
    private const string EmbedPath = "/embed";

    private readonly HttpClient _client;
    private readonly JsonHttpContext _context;

    public HttpEmbeddingService(HttpClient client, EmbeddingModelDescriptor model)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        Model = model ?? throw new ArgumentNullException(nameof(model));

        if (_client.BaseAddress is null)
        {
            throw new EmbeddingServiceException(
                "Le client HTTP du service d'embeddings n'a pas d'adresse de base. " +
                "Configure AssistantQR:Embeddings:ServiceUrl (par defaut http://localhost:8088).");
        }

        _context = new JsonHttpContext(
            "Le service d'embeddings",
            "Demarre le service Python du dossier python/, ou bascule AssistantQR:Profile sur « offline » " +
            "pour utiliser l'embedding factice.",
            (message, inner) => new EmbeddingServiceException(message, inner));
    }

    public EmbeddingModelDescriptor Model { get; }

    public async Task<EmbeddingVector> EmbedQueryAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new EmbeddingServiceException(
                "Impossible de calculer un embedding : la question est vide. " +
                "Le service refuserait la requete avec le code EMPTY_INPUT ; autant le dire tout de suite.");
        }

        var vectors = await EmbedAsync(new[] { text }, "query", cancellationToken).ConfigureAwait(false);
        return vectors[0];
    }

    public async Task<IReadOnlyList<EmbeddingVector>> EmbedDocumentsAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        if (texts is null)
        {
            throw new ArgumentNullException(nameof(texts));
        }

        if (texts.Count == 0)
        {
            // Un lot vide est une question sans objet, pas une erreur : on evite un
            // aller-retour reseau que le service rejetterait de toute facon.
            return Array.Empty<EmbeddingVector>();
        }

        return await EmbedAsync(texts, "document", cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<EmbeddingVector>> EmbedAsync(
        IReadOnlyList<string> texts,
        string kind,
        CancellationToken cancellationToken)
    {
        var response = await JsonHttp
            .PostAsync<EmbedRequest, EmbedResponse>(
                _client, EmbedPath, new EmbedRequest(texts, kind), _context, cancellationToken)
            .ConfigureAwait(false);

        var vectors = response.Vectors;

        if (vectors is null || vectors.Count != texts.Count)
        {
            throw new EmbeddingServiceException(
                $"Le service d'embeddings a renvoye {vectors?.Count ?? 0} vecteur(s) pour {texts.Count} texte(s). " +
                "L'appariement texte/vecteur repose sur l'ordre : un ecart rend l'index inexploitable.");
        }

        if (response.Dimension != Model.Dimension)
        {
            throw new EmbeddingServiceException(
                $"Le service d'embeddings annonce une dimension de {response.Dimension} alors que " +
                $"« {Model.Name} » est configure en {Model.Dimension}. " +
                "Corrige AssistantQR:Embeddings:Dimension, puis relance une indexation complete du corpus.");
        }

        var result = new EmbeddingVector[vectors.Count];

        for (var i = 0; i < vectors.Count; i++)
        {
            var values = vectors[i];

            if (values is null || values.Length == 0)
            {
                throw new EmbeddingServiceException(
                    $"Le service d'embeddings a renvoye un vecteur vide en position {i}.");
            }

            if (values.Length != Model.Dimension)
            {
                throw new EmbeddingServiceException(
                    $"Le vecteur en position {i} a {values.Length} coordonnees au lieu de {Model.Dimension}.");
            }

            result[i] = EmbeddingVector.From(values);
        }

        return result;
    }

    // Les DTO sont prives et locaux a l'adaptateur : ils decrivent le format du fil, pas
    // un concept du systeme. Le passage en snake_case est fait par les options partagees
    // de JsonHttp, ce qui evite une ribambelle d'attributs sur chaque propriete.
    private sealed record EmbedRequest(IReadOnlyList<string> Texts, string Kind);

    private sealed record EmbedResponse(string? Model, int Dimension, List<float[]?>? Vectors);
}
