using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AssistantQR.Infrastructure.Http;

/// <summary>
/// Plomberie JSON commune aux trois adaptateurs reseau de l'infrastructure.
/// </summary>
/// <remarks>
/// POURQUOI CE HELPER EXISTE : les trois adaptateurs (embeddings, index, modele de
/// langue) parlent a des services differents mais commettent les memes erreurs —
/// oublier le charset, avaler le corps d'erreur, remonter un « 500 » nu. Centraliser
/// la serialisation ET la traduction des pannes garantit que les trois racontent la
/// meme histoire a l'utilisateur.
///
/// Ce type est <c>internal</c> : il n'appartient a aucun port, il ne doit fuir ni vers
/// l'Application ni vers la ligne de commande. Ce qui sort d'ici, ce sont des exceptions
/// publiques (une par adaptateur) portant une phrase francaise actionnable.
/// </remarks>
internal static class JsonHttp
{
    /// <summary>
    /// Options partagees. <c>SnakeCaseLower</c> est le point de contact avec le contrat
    /// HTTP du service Python : les DTO restent nommes en anglais PascalCase cote C# et
    /// deviennent <c>chunk_id</c>, <c>top_k</c>, <c>max_access_level</c> sur le fil.
    /// L'encodeur relache garde les accents lisibles dans les corps envoyes, ce qui rend
    /// les traces reseau exploitables pendant le cours.
    /// </summary>
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private const int MaxRawBodyLength = 300;

    internal static async Task<TResponse> PostAsync<TRequest, TResponse>(
        HttpClient client,
        string path,
        TRequest payload,
        JsonHttpContext context,
        CancellationToken cancellationToken)
        where TResponse : class
    {
        var json = JsonSerializer.Serialize(payload, Options);
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        return await SendAsync<TResponse>(client, request, context, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<TResponse> GetAsync<TResponse>(
        HttpClient client,
        string path,
        JsonHttpContext context,
        CancellationToken cancellationToken)
        where TResponse : class
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        return await SendAsync<TResponse>(client, request, context, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<TResponse> SendAsync<TResponse>(
        HttpClient client,
        HttpRequestMessage request,
        JsonHttpContext context,
        CancellationToken cancellationToken)
        where TResponse : class
    {
        HttpResponseMessage response;
        string body;

        try
        {
            response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            // Connexion refusee, DNS, TLS : le service n'est pas la. C'est la panne la plus
            // frequente en salle, donc celle qui merite le message le plus precis.
            throw context.CreateException(
                $"{context.ServiceLabel} est injoignable sur {Describe(client)}. {context.UnreachableRemedy}",
                exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Delai depasse cote HttpClient : a distinguer d'une annulation demandee par
            // l'appelant, sinon on accuse le reseau d'un Ctrl+C.
            throw context.CreateException(
                $"{context.ServiceLabel} n'a pas repondu dans le delai imparti " +
                $"({client.Timeout.TotalSeconds:0} s) sur {Describe(client)}. " +
                "Augmente le delai dans la configuration, ou choisis un modele plus petit.",
                exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw context.CreateException(BuildFailureMessage(context, request, response.StatusCode, body), null);
            }

            TResponse? result;

            try
            {
                result = JsonSerializer.Deserialize<TResponse>(body, Options);
            }
            catch (JsonException exception)
            {
                throw context.CreateException(
                    $"{context.ServiceLabel} a renvoye un corps JSON illisible " +
                    $"({request.Method} {request.RequestUri}). {Truncate(body)}",
                    exception);
            }

            if (result is null)
            {
                throw context.CreateException(
                    $"{context.ServiceLabel} a repondu « null » la ou un objet JSON etait attendu " +
                    $"({request.Method} {request.RequestUri}).",
                    null);
            }

            return result;
        }
    }

    private static string BuildFailureMessage(
        JsonHttpContext context,
        HttpRequestMessage request,
        HttpStatusCode status,
        string body)
    {
        var (detail, code) = DescribeErrorBody(body);

        // Un adaptateur peut s'approprier certains statuts : Ollama sait que 404 signifie
        // « modele absent », le service Python ne le sait pas. On laisse donc l'appelant
        // fabriquer la phrase quand il en sait plus que nous.
        var specialised = context.DescribeStatus?.Invoke((int)status, code);
        if (!string.IsNullOrWhiteSpace(specialised))
        {
            return specialised;
        }

        var remedy = RemedyFor(code);
        var message =
            $"{context.ServiceLabel} a repondu {(int)status} ({status}) sur {request.Method} {request.RequestUri}. {detail}";

        return remedy.Length == 0 ? message : $"{message} {remedy}";
    }

    /// <summary>
    /// Decode le corps d'erreur du contrat partage — un objet <c>error</c> portant
    /// <c>code</c>, <c>message</c> et <c>hint</c> — et tolere la forme d'Ollama, qui
    /// envoie un <c>error</c> textuel. Ne leve jamais : un corps d'erreur mal forme ne
    /// doit pas masquer l'erreur qu'il decrit.
    /// </summary>
    internal static (string Detail, string? Code) DescribeErrorBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return ("Le service n'a renvoye aucun corps exploitable.", null);
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String)
                {
                    return ($"Message du service : « {error.GetString()} ».", null);
                }

                if (error.ValueKind == JsonValueKind.Object)
                {
                    return DescribeErrorObject(error);
                }
            }
        }
        catch (JsonException)
        {
            // Corps non JSON (page HTML d'un proxy, trace de pile Python...) : on retombe
            // sur le texte brut tronque, qui reste plus utile que rien.
        }

        return ($"Corps brut : {Truncate(body)}", null);
    }

    /// <summary>
    /// Traduit les codes d'erreur du contrat partage en geste a faire. C'est la difference
    /// entre un message d'erreur et un message d'aide.
    /// </summary>
    internal static string RemedyFor(string? code) => code switch
    {
        "OLLAMA_UNREACHABLE" =>
            "Le service Python ne joint pas Ollama : demarre « ollama serve ».",
        "MODEL_NOT_FOUND" =>
            "Le modele demande n'est pas installe cote service : installe-le, ou corrige la configuration du modele.",
        "DIMENSION_MISMATCH" =>
            "L'index a ete construit avec une autre dimension : relance une indexation complete du corpus.",
        "EMPTY_INPUT" =>
            "Le service a recu une liste de textes vide : il n'y a rien a encoder.",
        "INDEX_EMPTY" =>
            "L'index ne contient aucun morceau : indexe le corpus avant d'interroger l'assistant.",
        "INVALID_ACCESS_LEVEL" =>
            "Niveau d'acces refuse par le service : attendu « public », « internal » ou « confidential ».",
        _ => string.Empty,
    };

    private static (string Detail, string? Code) DescribeErrorObject(JsonElement error)
    {
        var code = ReadString(error, "code");
        var message = ReadString(error, "message");
        var hint = ReadString(error, "hint");

        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(code))
        {
            parts.Add($"code {code}");
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            parts.Add($"message « {message} »");
        }

        if (!string.IsNullOrWhiteSpace(hint))
        {
            parts.Add($"piste « {hint} »");
        }

        return parts.Count == 0
            ? ("Le corps d'erreur ne contient rien d'exploitable.", code)
            : ($"Detail du service : {string.Join(", ", parts)}.", code);
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Describe(HttpClient client) =>
        client.BaseAddress?.ToString() ?? "une adresse de base non configuree";

    private static string Truncate(string body)
    {
        var compact = body.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return compact.Length <= MaxRawBodyLength ? compact : compact[..MaxRawBodyLength] + "…";
    }
}
