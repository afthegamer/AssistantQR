using System.Text.Json.Serialization;

using AssistantQR.Application.Model;
using AssistantQR.Application.Ports;
using AssistantQR.Infrastructure.Http;

namespace AssistantQR.Infrastructure.LanguageModels;

/// <summary>
/// Adaptateur REEL de <see cref="ILanguageModel"/> : Ollama, en local, via
/// <c>POST /api/generate</c>.
/// </summary>
/// <remarks>
/// POURQUOI CETTE CLASSE EST LA FRONTIERE : c'est le seul fichier du depot qui sache
/// qu'un modele de langue se pilote par HTTP, que le champ s'appelle <c>num_predict</c>
/// et que le serveur ecoute sur le port 11434. Basculer vers un autre fournisseur ne
/// touche ni l'Application ni le Domain — la preuve, le profil « offline » remplace
/// cette classe par un extracteur de trente lignes sans qu'aucun cas d'usage ne bouge.
///
/// TENSION 1 (non-determinisme) : temperature, graine et nombre de jetons sont
/// transmis tels que le pipeline les a decides, jamais reecrits en douce. Un adaptateur
/// qui forcerait sa propre temperature ferait mentir l'empreinte de configuration
/// enregistree dans les instantanes — et rendrait toute comparaison caduque. Noter que
/// meme avec temperature 0 et graine fixe, Ollama ne garantit pas la reproductibilite
/// entre deux versions du serveur ou deux machines : le determinisme complet, seuls les
/// faux hors-ligne le donnent.
///
/// Le contrat externe d'Ollama est fige et ne nous appartient pas : les noms de champs
/// sont donc epingles explicitement plutot que confies a une convention de nommage
/// qu'un autre adaptateur pourrait changer un jour.
/// </remarks>
public sealed class OllamaLanguageModel : ILanguageModel
{
    private const string GeneratePath = "/api/generate";

    private readonly HttpClient _client;
    private readonly JsonHttpContext _context;

    public OllamaLanguageModel(HttpClient client, string modelId)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));

        if (string.IsNullOrWhiteSpace(modelId))
        {
            throw new ArgumentException(
                "Le nom du modele Ollama est obligatoire (par exemple « granite4.2:3b ») : " +
                "il finit dans l'empreinte de configuration des instantanes.",
                nameof(modelId));
        }

        ModelId = modelId.Trim();

        if (_client.BaseAddress is null)
        {
            throw new LanguageModelException(
                "Le client HTTP d'Ollama n'a pas d'adresse de base. " +
                "Configure AssistantQR:LanguageModel:OllamaUrl (par defaut http://localhost:11434).");
        }

        var model = ModelId;

        _context = new JsonHttpContext(
            "Ollama",
            $"Demarre-le (« ollama serve »), verifie que le modele est installe " +
            $"(« ollama pull {model} »), ou bascule AssistantQR:Profile sur « offline ».",
            (message, inner) => new LanguageModelException(message, inner))
        {
            // Ollama sait des choses que la plomberie generique ignore : chez lui, un 404
            // sur /api/generate ne veut pas dire « mauvaise URL » mais « modele absent ».
            // Traduire ce statut ici, c'est transformer une enigme en commande a taper.
            DescribeStatus = (status, _) => status switch
            {
                404 => $"Ollama a repondu 404 : le modele {model} n'est pas installe. " +
                       $"Lance : ollama pull {model}",
                400 => $"Ollama a repondu 400 : la requete a ete refusee pour le modele {model}. " +
                       "Verifie le nom du modele (« ollama list ») et les options envoyees.",
                _ => null,
            },
        };
    }

    public string ModelId { get; }

    public async Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var payload = new GenerateRequest(
            ModelId,
            request.Prompt,
            // stream=false : on veut la reponse d'un bloc. Le streaming serait un confort
            // d'interface, pas un besoin du pipeline, et il compliquerait la trace.
            false,
            // think=false : les modeles a raisonnement (granite4.2 en est un) emettent
            // sinon leur brouillon AVANT la reponse, dans le meme champ. Trois degats,
            // constates au premier contact avec le vrai modele :
            //   - la reponse rendue est le brouillon, en anglais, et non la reponse ;
            //   - le brouillon recopie le bloc d'extraits, donc ModelResponseParser y
            //     trouve des [identifiants] et fabrique des citations a partir d'un
            //     raisonnement, pas d'une reponse ;
            //   - le cout : 155 s au lieu de 1 s pour la meme question.
            // Le port ILanguageModel promet une reponse, pas un scratchpad. Le champ est
            // accepte aussi par les modeles qui ne raisonnent pas : il est envoye
            // inconditionnellement, sans negociation ni repli.
            false,
            new GenerateOptions(
                request.Temperature,
                request.Seed,
                request.MaxTokens,
                request.Stop is { Count: > 0 } ? request.Stop : null));

        var response = await JsonHttp
            .PostAsync<GenerateRequest, GenerateResponse>(
                _client, GeneratePath, payload, _context, cancellationToken)
            .ConfigureAwait(false);

        // Une reponse vide n'est PAS une panne d'infrastructure : c'est un modele qui n'a
        // rien produit. On la laisse remonter telle quelle jusqu'a AnswerPolicy, qui la
        // refusera proprement (ModelProducedEmptyAnswer) avec une explication lisible.
        // Lever une exception ici escamoterait une regle metier derriere une erreur
        // technique.
        return new LlmCompletion(
            StripReasoning(response.Response ?? string.Empty),
            string.IsNullOrWhiteSpace(response.Model) ? ModelId : response.Model,
            response.PromptEvalCount,
            response.EvalCount);
    }

    /// <summary>
    /// Filet de securite : retire un brouillon de raisonnement qu'un modele aurait emis
    /// malgre <c>think=false</c>, en ne gardant que ce qui suit la derniere balise
    /// fermante.
    /// </summary>
    /// <remarks>
    /// Ce n'est pas de la cosmetique. Le brouillon d'un modele a raisonnement recopie
    /// volontiers le bloc d'extraits qu'on vient de lui donner ; les [identifiants] qu'il
    /// contient sont alors indiscernables de vraies citations pour l'analyseur. Sans ce
    /// filtre, une reponse peut se retrouver « citee » sur la foi d'un raisonnement.
    ///
    /// On ne coupe QUE si la balise est presente, et on ne touche a rien d'autre : un
    /// adaptateur qui reecrirait la sortie du modele ferait mentir la trace.
    /// </remarks>
    private static string StripReasoning(string raw)
    {
        const string closing = "</think>";

        var last = raw.LastIndexOf(closing, StringComparison.OrdinalIgnoreCase);

        return last < 0 ? raw : raw[(last + closing.Length)..].TrimStart();
    }

    private sealed record GenerateRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("prompt")] string Prompt,
        [property: JsonPropertyName("stream")] bool Stream,
        [property: JsonPropertyName("think")] bool Think,
        [property: JsonPropertyName("options")] GenerateOptions Options);

    private sealed record GenerateOptions(
        [property: JsonPropertyName("temperature")] double Temperature,
        // Graine absente : on omet le champ plutot que d'envoyer null, qu'Ollama
        // n'interprete pas de la meme facon selon les versions.
        [property: JsonPropertyName("seed")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Seed,
        [property: JsonPropertyName("num_predict")] int NumPredict,
        [property: JsonPropertyName("stop")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Stop);

    private sealed record GenerateResponse(
        [property: JsonPropertyName("response")] string? Response,
        [property: JsonPropertyName("model")] string? Model,
        [property: JsonPropertyName("prompt_eval_count")] int? PromptEvalCount,
        [property: JsonPropertyName("eval_count")] int? EvalCount);
}
