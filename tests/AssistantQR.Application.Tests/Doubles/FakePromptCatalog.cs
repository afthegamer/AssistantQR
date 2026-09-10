using AssistantQR.Application.Model;
using AssistantQR.Application.Ports;

namespace AssistantQR.Application.Tests.Doubles;

/// <summary>
/// Catalogue de gabarits tenu en memoire.
///
/// Il rappelle une chose que le port <see cref="IPromptCatalog"/> affirme deja : un
/// prompt est une DONNEE fournie au pipeline, pas une constante compilee dedans. Les
/// tests peuvent donc faire varier le gabarit — sa version, son corps, son empreinte —
/// exactement comme un exploitant le ferait en editant un fichier, et observer l'effet
/// sur les instantanes.
/// </summary>
public sealed class FakePromptCatalog : IPromptCatalog
{
    /// <summary>
    /// Corps du gabarit par defaut. Il impose les trois contraintes du projet : reponse
    /// en francais, citations reprises telles quelles, marqueur de refus si les extraits
    /// ne suffisent pas.
    /// </summary>
    public const string DefaultBody = """
        Tu es l'assistant documentaire de la mediatheque. Reponds en francais.

        Question : {{question}}

        Extraits disponibles :
        {{evidence}}

        Cite chaque source entre crochets en reprenant exactement son identifiant.
        N'invente jamais d'identifiant. Si les extraits ne suffisent pas, reponds
        {{refusal_marker}} seul.
        """;

    private readonly List<PromptTemplate> _templates;

    /// <summary>Construit un catalogue a partir des gabarits fournis.</summary>
    public FakePromptCatalog(params PromptTemplate[] templates) => _templates = new List<PromptTemplate>(templates);

    /// <summary>Le gabarit attendu par <c>PipelineOptions.Default</c>, en version 1.0.0.</summary>
    public static PromptTemplate DefaultTemplate { get; } = new(
        "answer-with-citations",
        "1.0.0",
        DefaultBody,
        new[] { "question", "evidence", "refusal_marker" });

    /// <summary>Catalogue minimal contenant le gabarit par defaut.</summary>
    public static FakePromptCatalog Default() => new(DefaultTemplate);

    /// <inheritdoc />
    public PromptTemplate Get(string name, string version)
    {
        foreach (var template in _templates)
        {
            if (template.Name == name && template.Version == version)
            {
                return template;
            }
        }

        throw new InvalidOperationException($"Gabarit introuvable : {name}@{version}.");
    }

    /// <inheritdoc />
    public PromptTemplate GetLatest(string name)
    {
        PromptTemplate? best = null;
        foreach (var template in _templates)
        {
            if (template.Name != name)
            {
                continue;
            }

            if (best is null || Version.Parse(template.Version) > Version.Parse(best.Version))
            {
                best = template;
            }
        }

        return best ?? throw new InvalidOperationException($"Aucun gabarit nomme « {name} » dans le catalogue.");
    }

    /// <inheritdoc />
    public IReadOnlyList<PromptDescriptor> List()
    {
        var descriptors = new List<PromptDescriptor>(_templates.Count);
        foreach (var template in _templates)
        {
            descriptors.Add(template.Describe());
        }

        return descriptors;
    }
}
