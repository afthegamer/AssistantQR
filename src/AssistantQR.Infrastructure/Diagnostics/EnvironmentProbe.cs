using System.Text.Json;

using AssistantQR.Infrastructure.Configuration;
using AssistantQR.Infrastructure.DependencyInjection;

namespace AssistantQR.Infrastructure.Diagnostics;

/// <summary>Verdict d'une sonde : le composant teste, s'il est utilisable, et pourquoi.</summary>
/// <remarks>
/// Trois champs suffisent parce que la sonde ne pretend pas diagnostiquer : elle
/// constate. Le <c>Detail</c> est une phrase francaise destinee a etre lue telle quelle
/// dans un terminal, pas un code d'erreur a interpreter.
/// </remarks>
public sealed record ProbeResult(string Component, bool Ok, string Detail);

/// <summary>
/// Verifie que l'environnement peut faire tourner l'assistant : les dossiers sont-ils la,
/// les services repondent-ils, le modele demande est-il installe ?
/// </summary>
/// <remarks>
/// POURQUOI UNE SONDE PLUTOT QUE DES MESSAGES D'ERREUR AU FIL DE L'EAU. Sans elle, un
/// environnement mal prepare se manifeste par la premiere panne rencontree, qui n'est
/// presque jamais la plus informative : on apprend qu'Ollama ne repond pas, on l'installe,
/// on relance, et l'on decouvre alors que le dossier des gabarits est introuvable. La
/// sonde donne l'etat complet d'un seul coup, et transforme une serie de decouvertes en
/// une liste de courses.
///
/// ELLE NE LEVE JAMAIS, ET C'EST SA REGLE CENTRALE. Un outil de diagnostic qui echoue sur
/// le premier composant casse ne diagnostique rien. Toute exception — reseau, delai,
/// JSON illisible, URL absurde — est donc rattrapee et retraduite en <c>Ok = false</c>
/// accompagne de la phrase qui dit quoi faire.
///
/// ELLE NE SONDE QUE CE QUE LA CONFIGURATION SOLLICITE. En profil « offline », annoncer
/// « Ollama injoignable » en rouge serait faux : rien ne le demande. La sonde reprend donc
/// les memes predicats que le montage — c'est volontairement le meme code, pour qu'elle ne
/// puisse pas dire une chose pendant que le montage en fait une autre.
/// </remarks>
public sealed class EnvironmentProbe
{
    /// <summary>
    /// Delai court, propre a la sonde. Les clients nommes accordent deux a trois minutes
    /// a une generation ; attendre aussi longtemps pour savoir si un service repond
    /// transformerait le diagnostic en epreuve de patience.
    /// </summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(4);

    private readonly AssistantOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>Construit la sonde a partir de la configuration effective du programme.</summary>
    public EnvironmentProbe(AssistantOptions options, IHttpClientFactory httpClientFactory)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
    }

    /// <summary>Execute les quatre sondes, dans l'ordre ou l'on veut les lire.</summary>
    public async Task<IReadOnlyList<ProbeResult>> RunAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<ProbeResult>(4)
        {
            ProbeDirectory("Corpus", _options.CorpusDirectory, "*.md", "document"),
            ProbeDirectory("Gabarits de prompt", _options.PromptsDirectory, "*@*.md", "gabarit"),
        };

        results.Add(await ProbeEmbeddingServiceAsync(cancellationToken).ConfigureAwait(false));
        results.Add(await ProbeOllamaAsync(cancellationToken).ConfigureAwait(false));

        return results;
    }

    /// <summary>
    /// Un dossier absent est la panne la plus frequente, et la plus deroutante : le chemin
    /// est relatif, il est resolu par <see cref="PathResolver"/>, et l'utilisateur n'a
    /// aucun moyen de deviner ou le programme a regarde. La sonde affiche donc le chemin
    /// absolu, toujours — c'est la moitie de la reponse.
    /// </summary>
    private static ProbeResult ProbeDirectory(string component, string configured, string pattern, string noun)
    {
        try
        {
            var directory = PathResolver.Resolve(configured);

            if (!Directory.Exists(directory))
            {
                return new ProbeResult(component, false,
                    $"Dossier introuvable : « {directory} » (configuré : « {configured} »).");
            }

            // Le README qui explique un dossier n'est ni un document du corpus ni un
            // gabarit : les adaptateurs l'ignorent, la sonde doit compter comme eux.
            var count = Directory.GetFiles(directory, pattern, SearchOption.AllDirectories)
                                 .Count(file => !string.Equals(
                                     Path.GetFileName(file), "README.md", StringComparison.OrdinalIgnoreCase));

            return count == 0
                ? new ProbeResult(component, false,
                    $"Le dossier « {directory} » ne contient aucun fichier « {pattern} ».")
                : new ProbeResult(component, true, $"{count} {noun}(s) dans « {directory} ».");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or ArgumentException or NotSupportedException)
        {
            return new ProbeResult(component, false, $"Lecture impossible : {exception.Message}");
        }
    }

    /// <summary>
    /// Sonde <c>GET /health</c> du service Python. Le corps annonce le modele et la
    /// dimension : les rappeler ici permet de reperer d'un coup d'oeil un desaccord avec
    /// la configuration, qui est la cause du scenario B.
    /// </summary>
    private async Task<ProbeResult> ProbeEmbeddingServiceAsync(CancellationToken cancellationToken)
    {
        const string component = "Service Python (embeddings + index)";

        if (!ServiceCollectionExtensions.UsesRemoteEmbeddings(_options))
        {
            return new ProbeResult(component, true,
                $"Non sollicité : les vecteurs sont calculés par « {EmbeddingOptions.HashingModel} » " +
                "et l'index vit en mémoire.");
        }

        var (body, failure) = await ReadJsonAsync(
            _options.Embeddings.ServiceUrl,
            "/health",
            ServiceCollectionExtensions.EmbeddingsClientName,
            "Démarre-le : scripts/start-embeddings.ps1 (ou .sh), ou bascule AssistantQR:Profile sur « offline ».",
            cancellationToken).ConfigureAwait(false);

        if (failure is not null)
        {
            return new ProbeResult(component, false, failure);
        }

        var model = ReadString(body, "embedding_model") ?? "(modèle non annoncé)";
        var dimension = ReadInt32(body, "dimension");
        var chunkCount = ReadInt32(body, "index_chunk_count");

        var detail =
            $"Répond sur {_options.Embeddings.ServiceUrl} : modèle « {model} », dimension {dimension}, " +
            $"{chunkCount} morceau(x) indexé(s).";

        // Un desaccord de nom ou de dimension n'est pas une panne du service : c'est une
        // incoherence entre lui et notre configuration. On le signale sans le confondre
        // avec un service injoignable.
        if (!string.Equals(model, _options.Embeddings.Model?.Trim(), StringComparison.OrdinalIgnoreCase) ||
            dimension != _options.Embeddings.Dimension)
        {
            return new ProbeResult(component, false,
                detail + $" Or la configuration demande « {_options.Embeddings.Model} » en dimension " +
                $"{_options.Embeddings.Dimension} : aligne les deux, puis réindexe le corpus.");
        }

        return new ProbeResult(component, true, detail);
    }

    /// <summary>
    /// Sonde <c>GET /api/tags</c> d'Ollama. Le service peut tres bien repondre sans que le
    /// modele demande soit installe : c'est le cas le plus frequent, et il merite une
    /// phrase qui donne la commande a taper.
    /// </summary>
    private async Task<ProbeResult> ProbeOllamaAsync(CancellationToken cancellationToken)
    {
        const string component = "Ollama (modèle de langue)";

        var configuredModel = _options.LanguageModel.Model?.Trim() ?? string.Empty;

        if (!ServiceCollectionExtensions.UsesOllama(_options))
        {
            return new ProbeResult(component, true,
                $"Non sollicité : le modèle « {configuredModel} » est un faux déterministe, hors ligne.");
        }

        var (body, failure) = await ReadJsonAsync(
            _options.LanguageModel.OllamaUrl,
            "/api/tags",
            ServiceCollectionExtensions.OllamaClientName,
            "Démarre-le (« ollama serve »), ou bascule AssistantQR:Profile sur « offline ».",
            cancellationToken).ConfigureAwait(false);

        if (failure is not null)
        {
            return new ProbeResult(component, false, failure);
        }

        var installed = ReadModelNames(body);

        if (installed.Count == 0)
        {
            return new ProbeResult(component, false,
                $"Ollama répond sur {_options.LanguageModel.OllamaUrl} mais n'a aucun modèle installé. " +
                $"Lance : ollama pull {configuredModel}");
        }

        // Ollama complete un nom sans etiquette par « :latest ». Comparer les deux formes
        // evite d'annoncer « modele absent » pour une simple difference d'ecriture.
        var present = installed.Any(name =>
            string.Equals(name, configuredModel, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, configuredModel + ":latest", StringComparison.OrdinalIgnoreCase));

        return present
            ? new ProbeResult(component, true,
                $"Répond sur {_options.LanguageModel.OllamaUrl}, modèle « {configuredModel} » installé " +
                $"({installed.Count} modèle(s) au total).")
            : new ProbeResult(component, false,
                $"Ollama répond mais « {configuredModel} » n'est pas installé. " +
                $"Installés : {string.Join(", ", installed)}. Lance : ollama pull {configuredModel}");
    }

    /// <summary>
    /// Fait l'appel et rend soit le document JSON, soit une phrase d'echec — jamais une
    /// exception. L'URL est construite en absolu : la sonde doit fonctionner meme si le
    /// client nomme n'a pas ete configure par le montage.
    /// </summary>
    private async Task<(JsonElement Body, string? Failure)> ReadJsonAsync(
        string baseUrl,
        string path,
        string clientName,
        string remedy,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out var root))
        {
            return (default, $"Adresse invalide dans la configuration : « {baseUrl} ».");
        }

        var target = new Uri(root, path);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);

        try
        {
            var client = _httpClientFactory.CreateClient(clientName);

            using var response = await client.GetAsync(target, timeout.Token).ConfigureAwait(false);
            var payload = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return (default,
                    $"{target} a répondu {(int)response.StatusCode} ({response.StatusCode}). {remedy}");
            }

            using var document = JsonDocument.Parse(payload);

            // Le document est libere a la sortie de ce bloc : on en garde une copie
            // autonome, seule facon de rendre l'element sans laisser fuiter le document.
            return (document.RootElement.Clone(), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return (default, "Sonde interrompue.");
        }
        catch (OperationCanceledException)
        {
            return (default,
                $"{target} n'a pas répondu en moins de {ProbeTimeout.TotalSeconds:0} secondes. {remedy}");
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException
                                              or InvalidOperationException or UriFormatException)
        {
            return (default, $"{target} est injoignable ({exception.Message}) {remedy}");
        }
    }

    private static List<string> ReadModelNames(JsonElement body)
    {
        var names = new List<string>();

        if (body.ValueKind != JsonValueKind.Object ||
            !body.TryGetProperty("models", out var models) ||
            models.ValueKind != JsonValueKind.Array)
        {
            return names;
        }

        foreach (var entry in models.EnumerateArray())
        {
            var name = ReadString(entry, "name") ?? ReadString(entry, "model");
            if (!string.IsNullOrWhiteSpace(name))
            {
                names.Add(name);
            }
        }

        return names;
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int ReadInt32(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var number)
            ? number
            : 0;
}
