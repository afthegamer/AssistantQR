using System.Text.Json;

using AssistantQR.Application.Model;
using AssistantQR.Application.Ports;
using AssistantQR.Infrastructure.Embeddings;
using AssistantQR.Infrastructure.Http;

namespace AssistantQR.Infrastructure.LanguageModels;

/// <summary>
/// Adaptateur FACTICE de rejeu : sert des reponses enregistrees a l'avance, et delegue
/// a un autre <see cref="ILanguageModel"/> ce qu'il ne connait pas.
/// </summary>
/// <remarks>
/// A QUOI SERT UN MODELE DE REJEU. Un vrai modele coute du temps et varie ; un faux
/// extractif est stable mais ne dit jamais rien d'inattendu. Le rejeu occupe la place
/// entre les deux : on y colle des reponses REELLES capturees une fois, et l'on obtient
/// une demonstration a la fois realiste et reproductible. C'est aussi le seul moyen
/// commode de fabriquer des cas pathologiques a la demande — une reponse qui cite un
/// document inexistant, une qui cite un document confidentiel, une qui ne cite rien.
/// Chacun de ces trois cas declenche un <c>RefusalReason</c> different, et c'est ainsi
/// qu'on montre qu'<c>AnswerPolicy</c> tient sans dependre du modele.
///
/// LE REPLI (fallback) EST LA PIECE IMPORTANTE. Sans lui, une question absente du jeu
/// de rejeu ferait s'arreter la demonstration. Avec lui, on repond quand meme —
/// typiquement via l'extracteur — et le systeme reste utilisable de bout en bout.
/// Quand le repli intervient, on renvoie sa reponse telle quelle, ModelId compris :
/// la trace doit dire quel modele a REELLEMENT parle, pas quel modele etait configure.
///
/// L'appariement est insensible a la casse ET aux accents : un jeu de rejeu ecrit
/// « horaires » doit reconnaitre une question qui parle d'« horaires d'été ». C'est le
/// meme repliement de texte que celui de l'embedding factice, et pour la meme raison.
/// </remarks>
public sealed class ReplayLanguageModel : ILanguageModel
{
    private static readonly JsonSerializerOptions FileOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly IReadOnlyList<ReplayEntry> _entries;
    private readonly string[] _foldedMatches;
    private readonly ILanguageModel? _fallback;

    public ReplayLanguageModel(IReadOnlyList<ReplayEntry> entries, ILanguageModel? fallback = null)
    {
        _entries = entries ?? throw new ArgumentNullException(nameof(entries));
        _fallback = fallback;

        // Le repliement des motifs est fait une fois, a la construction : la boucle
        // d'appariement s'execute a chaque question, elle n'a pas a le refaire.
        _foldedMatches = new string[_entries.Count];
        for (var i = 0; i < _entries.Count; i++)
        {
            _foldedMatches[i] = TextNormalization.Fold(_entries[i].Match);
        }
    }

    public string ModelId => "replay";

    /// <summary>
    /// Charge un jeu de rejeu depuis un fichier JSON : un tableau d'objets
    /// <c>{ "match": "...", "response": "..." }</c>.
    /// </summary>
    public static ReplayLanguageModel FromJsonFile(string path, ILanguageModel? fallback = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Le chemin du fichier de rejeu est obligatoire.", nameof(path));
        }

        if (!File.Exists(path))
        {
            throw new LanguageModelException(
                $"Fichier de rejeu introuvable : « {path} ». " +
                "Cree-le, corrige AssistantQR:LanguageModel:ReplayFile, ou choisis un autre modele " +
                "(« extractive-fake » fonctionne sans aucun fichier).");
        }

        List<ReplayEntry>? entries;

        try
        {
            entries = JsonSerializer.Deserialize<List<ReplayEntry>>(File.ReadAllText(path), FileOptions);
        }
        catch (JsonException exception)
        {
            throw new LanguageModelException(
                $"Le fichier de rejeu « {path} » n'est pas un JSON valide : {exception.Message} " +
                "Format attendu : un tableau d'objets { \"match\": \"...\", \"response\": \"...\" }.",
                exception);
        }
        catch (IOException exception)
        {
            throw new LanguageModelException(
                $"Lecture impossible du fichier de rejeu « {path} » : {exception.Message}",
                exception);
        }

        if (entries is null)
        {
            throw new LanguageModelException(
                $"Le fichier de rejeu « {path} » ne contient pas de tableau d'entrees.");
        }

        return new ReplayLanguageModel(entries, fallback);
    }

    public async Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var folded = TextNormalization.Fold(request.Prompt);

        for (var i = 0; i < _entries.Count; i++)
        {
            var needle = _foldedMatches[i];

            // Un motif vide apparierait tout : on l'ignore plutot que d'en faire un
            // fourre-tout involontaire. C'est le role du repli, et il est explicite.
            if (needle.Length == 0)
            {
                continue;
            }

            // Le PREMIER motif trouve gagne : l'ordre du fichier est donc significatif,
            // du plus specifique au plus general.
            if (folded.Contains(needle, StringComparison.Ordinal))
            {
                return new LlmCompletion(_entries[i].Response, ModelId);
            }
        }

        if (_fallback is not null)
        {
            return await _fallback.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
        }

        // Aucun motif, aucun repli : on refuse explicitement plutot que de rendre une
        // chaine vide, qui serait interpretee comme « le modele a repondu du vide ».
        return new LlmCompletion(ModelResponseParser.RefusalMarker, ModelId);
    }
}
