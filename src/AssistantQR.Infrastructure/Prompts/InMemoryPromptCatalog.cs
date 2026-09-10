using AssistantQR.Application.Model;
using AssistantQR.Application.Ports;

namespace AssistantQR.Infrastructure.Prompts;

/// <summary>
/// Adaptateur FACTICE de <see cref="IPromptCatalog"/> : des gabarits fournis a la main.
/// </summary>
/// <remarks>
/// Un test qui verifie le comportement du pipeline face a un modele qui refuse, ou qui
/// cite un document interdit, n'a aucune raison de dependre du texte reel des prompts.
/// Pire : s'il en dependait, retoucher une formulation dans <c>prompts/</c> ferait
/// echouer des tests qui ne parlent pas de prompts. Ce double coupe ce lien — le test
/// declare le gabarit minimal dont il a besoin, et rien d'autre.
///
/// Il conserve neanmoins la meme regle que l'adaptateur reel pour <c>GetLatest</c> :
/// un double dont le comportement diverge sur un point utile transforme les tests en
/// verification d'eux-memes.
/// </remarks>
public sealed class InMemoryPromptCatalog : IPromptCatalog
{
    private readonly Dictionary<string, PromptTemplate> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PromptTemplate> _templates = new();

    /// <summary>Construit le catalogue a partir de gabarits deja formes.</summary>
    public InMemoryPromptCatalog(params PromptTemplate[] templates)
    {
        ArgumentNullException.ThrowIfNull(templates);

        foreach (var template in templates)
        {
            if (template is null)
            {
                continue;
            }

            _byKey[$"{template.Name}@{template.Version}"] = template;
            _templates.Add(template);
        }
    }

    /// <inheritdoc />
    public PromptTemplate Get(string name, string version)
    {
        if (_byKey.TryGetValue($"{name?.Trim()}@{version?.Trim()}", out var template))
        {
            return template;
        }

        throw new InvalidOperationException($"Le gabarit « {name}@{version} » est absent du catalogue de test.");
    }

    /// <inheritdoc />
    public PromptTemplate GetLatest(string name)
    {
        PromptTemplate? best = null;
        var bestVersion = new Version(0, 0, 0);

        foreach (var template in _templates)
        {
            if (!string.Equals(template.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Une version illisible est traitee comme la plus ancienne possible :
            // un double ne doit pas exploser sur un detail de mise en forme.
            var parsed = Version.TryParse(template.Version, out var version) ? version : new Version(0, 0, 0);

            if (best is null || parsed > bestVersion)
            {
                best = template;
                bestVersion = parsed;
            }
        }

        if (best is null)
        {
            throw new InvalidOperationException($"Aucun gabarit ne porte le nom « {name} » dans le catalogue de test.");
        }

        return best;
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
