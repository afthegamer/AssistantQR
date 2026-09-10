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
/// L'adaptateur confronte l'etiquette qu'on lui a donnee (AssistantQR:Embeddings:Model
/// et :Dimension) a ce que le service annonce sur <c>GET /health</c>, une seule fois, au
/// premier calcul reel. Ce controle ne remplace pas la detection d'incoherence
/// modele/index faite en Application a partir des metadonnees de l'index : il couvre le
/// trou que celle-ci ne peut PAS voir, celui ou l'etiquette est fausse des l'origine et
/// donc partout coherente avec elle-meme. Voir la methode de verification plus bas.
/// </remarks>
public sealed class HttpEmbeddingService : IEmbeddingService
{
    private const string EmbedPath = "/embed";
    private const string HealthPath = "/health";

    private readonly HttpClient _client;
    private readonly JsonHttpContext _context;

    // Verrou d'une verification qui doit avoir lieu UNE fois pour la duree du processus :
    // interroger /health a chaque lot d'embeddings doublerait le nombre d'allers-retours
    // pour une information qui ne change pas tant que le service n'est pas redemarre.
    private readonly SemaphoreSlim _announcementGate = new(1, 1);
    private bool _announcementChecked;

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
        // Chemin commun de TOUTES les operations qui passent par le port : index, ask,
        // snapshot record, demonstrations. Placer le controle ici plutot que dans chaque
        // methode publique garantit qu'aucun appelant futur ne puisse le contourner par
        // oubli.
        await EnsureAnnouncedModelMatchesDeclarationAsync(cancellationToken).ConfigureAwait(false);

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

    /// <summary>
    /// Confronte le modele DECLARE dans la configuration au modele REELLEMENT servi par
    /// le service Python, et refuse de calculer le moindre vecteur en cas de desaccord.
    /// </summary>
    /// <remarks>
    /// POURQUOI CETTE VERIFICATION EXISTE : un adaptateur qui accepte une etiquette sans
    /// la confronter a la realite transforme une erreur de configuration en donnee fausse
    /// DURABLE. Le nom du modele vient d'ici, de la configuration C# ; les vecteurs, eux,
    /// viennent du service Python, qui a resolu SON modele au demarrage depuis
    /// EMBEDDING_MODEL. Si les deux divergent, l'etiquette declaree est ecrite telle
    /// quelle dans les metadonnees de l'index par <c>POST /index/reset</c> et dans
    /// l'empreinte de configuration des instantanes : tout devient coherent AVEC
    /// LUI-MEME, et le controle d'incoherence modele/index de l'Application — qui compare
    /// l'etiquette de l'index a l'etiquette configuree — ne se declenche jamais. La panne
    /// ne laisse aucune trace ; seul le sens des vecteurs est faux, et rien ne le dit.
    /// C'est la meme famille de mensonge que celui deja refuse dans la demonstration de
    /// bascule d'embeddings : une etiquette crue sur parole.
    ///
    /// La verification est PARESSEUSE (le constructeur reste synchrone et sans reseau,
    /// donc le montage des services ne depend pas d'un service demarre) et MEMORISEE
    /// (une seule fois par processus). Un echec, lui, n'est pas memorise : une panne
    /// reseau passagere ne doit pas condamner le processus, seul un succes ferme la porte.
    /// </remarks>
    private async Task EnsureAnnouncedModelMatchesDeclarationAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _announcementChecked))
        {
            return;
        }

        await _announcementGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_announcementChecked)
            {
                return;
            }

            var health = await JsonHttp
                .GetAsync<HealthResponse>(_client, HealthPath, _context, cancellationToken)
                .ConfigureAwait(false);

            var announcedName = (health.EmbeddingModel ?? string.Empty).Trim();
            var declaredName = Model.Name.Trim();

            // Comparaison insensible a la casse : « BGE-M3 » et « bge-m3 » designent le
            // meme modele cote Ollama. Ce qui est refuse ici est un desaccord de modele,
            // pas une difference de frappe.
            var sameName = announcedName.Length > 0 &&
                string.Equals(announcedName, declaredName, StringComparison.OrdinalIgnoreCase);
            var sameDimension = health.Dimension == Model.Dimension;

            if (!sameName || !sameDimension)
            {
                throw new EmbeddingServiceException(BuildMismatchMessage(announcedName, health.Dimension));
            }

            Volatile.Write(ref _announcementChecked, true);
        }
        finally
        {
            _announcementGate.Release();
        }
    }

    /// <summary>
    /// Fabrique le refus : les deux valeurs cote a cote, ce qui se serait passe sans ce
    /// controle, et les deux seules issues concretes.
    /// </summary>
    private string BuildMismatchMessage(string announcedName, int announcedDimension)
    {
        var announcedLabel = announcedName.Length == 0 ? "(aucun nom annonce)" : announcedName;
        var alignmentTarget = announcedName.Length == 0 ? Model.Name : announcedName;

        return
            "Le service d'embeddings ne sert pas le modele que la configuration declare, " +
            "et aucun vecteur ne sera calcule tant que les deux ne coincideront pas. " +
            $"Nom declare : « {Model.Name} » ; nom reellement servi : « {announcedLabel} ». " +
            $"Dimension declaree : {Model.Dimension} ; dimension reellement servie : {announcedDimension}. " +
            "CE QUI SE SERAIT PASSE SANS CE CONTROLE : les vecteurs seraient venus d'un modele " +
            "et l'etiquette d'un autre. L'etiquette declaree serait partie telle quelle dans les " +
            "metadonnees de l'index (POST /index/reset) et dans l'empreinte de configuration des " +
            "instantanes ; l'index et les instantanes auraient donc ete FAUX tout en restant " +
            "coherents entre eux, si bien que l'avertissement d'incoherence index/modele ne se " +
            "serait JAMAIS declenche. La donnee fausse aurait survecu a l'execution qui l'a produite. " +
            "DEUX ISSUES : (1) aligner la declaration sur le service, avec " +
            $"« --embedding {alignmentTarget} --dimension {announcedDimension} » ou " +
            $"« ASSISTANTQR_EMBEDDINGS__MODEL={alignmentTarget} » et " +
            $"« ASSISTANTQR_EMBEDDINGS__DIMENSION={announcedDimension} » ; (2) redemarrer le service " +
            $"sur le modele declare, avec « scripts/start-embeddings.ps1 -Model {Model.Name} ». " +
            "Dans les deux cas, relance ensuite une indexation complete du corpus : les vecteurs " +
            "deja ecrits viennent de l'autre modele.";
    }

    // Les DTO sont prives et locaux a l'adaptateur : ils decrivent le format du fil, pas
    // un concept du systeme. Le passage en snake_case est fait par les options partagees
    // de JsonHttp, ce qui evite une ribambelle d'attributs sur chaque propriete.
    private sealed record EmbedRequest(IReadOnlyList<string> Texts, string Kind);

    private sealed record EmbedResponse(string? Model, int Dimension, List<float[]?>? Vectors);

    // Seuls les deux champs qui nous concernent sont declares : le reste du corps de
    // /health (statut, joignabilite d'Ollama, taille de l'index) est du diagnostic, pas
    // une donnee dont cet adaptateur ait besoin pour decider.
    private sealed record HealthResponse(string? EmbeddingModel, int Dimension);
}
