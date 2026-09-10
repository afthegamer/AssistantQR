using System.Text;
using System.Text.RegularExpressions;

using AssistantQR.Application.Model;
using AssistantQR.Application.Ports;

namespace AssistantQR.Application.Tests.Doubles;

/// <summary>
/// Modele de langue qui n'invente rien : il relit le bloc d'extraits du prompt, garde
/// les deux premiers et les recopie en les citant.
///
/// POURQUOI CETTE DOUBLURE EN PLUS DE <see cref="ScriptedLanguageModel"/>.
/// Un modele a sortie figee est parfait pour tester les REFUS, mais il est aveugle a
/// ce qu'on lui montre : quoi qu'il arrive, il cite les memes identifiants. Or les
/// demonstrations du principe CACE ont besoin de l'inverse — un modele dont les
/// citations SUIVENT les extraits recuperes, pour qu'un changement de decoupage ou de
/// modele d'embeddings se propage jusqu'aux sources de la reponse finale.
///
/// Il est aussi la preuve que le format produit par <see cref="EvidenceFormatter"/>
/// est relisible : si ce format change, ce modele cesse de citer et les tests tombent.
/// </summary>
public sealed class EchoingLanguageModel : ILanguageModel
{
    private static readonly Regex EvidenceBlockPattern = new(
        @"\[(?<id>[A-Za-z0-9][A-Za-z0-9_\-]{0,127})\][^\n]*\((?:public|internal|confidential)\)\n(?<text>[^\n]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly List<LlmRequest> _requests = new();

    /// <summary>Construit un modele extractif nomme.</summary>
    public EchoingLanguageModel(string modelId = "echoing-fake") => ModelId = modelId;

    /// <inheritdoc />
    public string ModelId { get; }

    /// <summary>Nombre d'appels recus.</summary>
    public int CallCount => _requests.Count;

    /// <summary>La derniere requete recue.</summary>
    public LlmRequest? LastRequest => _requests.Count == 0 ? null : _requests[^1];

    /// <inheritdoc />
    public Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        _requests.Add(request);

        var matches = EvidenceBlockPattern.Matches(request.Prompt);
        if (matches.Count == 0)
        {
            // Aucun extrait relisible : le modele applique la consigne du prompt.
            return Task.FromResult(new LlmCompletion(ModelResponseParser.RefusalMarker, ModelId));
        }

        var builder = new StringBuilder("D'apres les documents consultes :");
        var taken = Math.Min(2, matches.Count);

        for (var i = 0; i < taken; i++)
        {
            var id = matches[i].Groups["id"].Value;
            var text = FirstSentence(matches[i].Groups["text"].Value);
            builder.Append(' ').Append(text).Append(" [").Append(id).Append(']');
        }

        return Task.FromResult(new LlmCompletion(builder.ToString(), ModelId));
    }

    private static string FirstSentence(string text)
    {
        var trimmed = text.Trim();
        var stop = trimmed.IndexOf('.');
        return stop < 0 ? trimmed : trimmed[..(stop + 1)];
    }
}
