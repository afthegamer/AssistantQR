using AssistantQR.Application.Model;
using AssistantQR.Application.Ports;

namespace AssistantQR.Application.Tests.Doubles;

/// <summary>
/// Modele de langue dont la sortie est ecrite d'avance, et qui conserve tout ce qu'on
/// lui a demande.
///
/// C'est la doublure qui rend le systeme TESTABLE malgre son maillon probabiliste.
/// En production, on ne peut rien affirmer sur le texte que rendra un modele ; on peut
/// en revanche affirmer tout le reste — ce que le pipeline fait de ce texte, quand il
/// le rejette, et ce qu'il lui envoie. En figeant la sortie, on rend le systeme entier
/// deterministe sauf a l'endroit ou l'on a choisi de ne pas l'etre.
///
/// <see cref="CallCount"/> n'est pas un gadget : la regle « quand rien n'est lisible,
/// on n'appelle pas le modele » ne se verifie QUE par un compteur. Un test qui
/// n'assertait que le refus passerait aussi pour une implementation qui appelle le
/// modele, ignore sa reponse et refuse quand meme — au prix d'une facture d'API et,
/// surtout, d'extraits interdits recopies dans le prompt d'un service tiers.
/// </summary>
public sealed class ScriptedLanguageModel : ILanguageModel
{
    private readonly List<LlmRequest> _requests = new();

    /// <summary>Construit un modele qui rendra toujours <paramref name="response"/>.</summary>
    public ScriptedLanguageModel(string response, string modelId = "scripted-fake")
    {
        Response = response;
        ModelId = modelId;
    }

    /// <inheritdoc />
    public string ModelId { get; }

    /// <summary>Le texte que le modele rendra au prochain appel.</summary>
    public string Response { get; set; }

    /// <summary>Nombre d'appels recus. Zero est une assertion a part entiere.</summary>
    public int CallCount => _requests.Count;

    /// <summary>Toutes les requetes recues, dans l'ordre.</summary>
    public IReadOnlyList<LlmRequest> Requests => _requests;

    /// <summary>La derniere requete recue, ou <c>null</c> si le modele n'a jamais ete appele.</summary>
    public LlmRequest? LastRequest => _requests.Count == 0 ? null : _requests[^1];

    /// <inheritdoc />
    public Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        _requests.Add(request);
        return Task.FromResult(new LlmCompletion(Response, ModelId, PromptTokens: 42, CompletionTokens: 7));
    }
}
