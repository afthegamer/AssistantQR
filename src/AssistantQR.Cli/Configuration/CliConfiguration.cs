using System.Globalization;

using AssistantQR.Cli.Commands;

using AssistantQR.Infrastructure.Configuration;

using Microsoft.Extensions.Configuration;

namespace AssistantQR.Cli.Configuration;

/// <summary>
/// Fabrique l'objet de configuration effectif : fichier, puis variables d'environnement,
/// puis options de ligne de commande.
/// </summary>
/// <remarks>
/// TROIS SOURCES, UN ORDRE, ET IL N'EST PAS ARBITRAIRE. Le fichier
/// <c>appsettings.json</c> porte le profil qui doit marcher pour quelqu'un qui vient de
/// cloner le depot : tout factice, aucun reseau. Les variables d'environnement portent ce
/// qui appartient a la MACHINE — l'adresse d'un service, un delai d'attente — et n'ont
/// aucune raison d'etre versionnees. Les options de ligne de commande portent ce qui
/// appartient a l'INVOCATION : la question du jour, le mode de filtrage qu'on veut
/// essayer. Chaque niveau ecrase le precedent parce qu'il est plus specifique que lui.
///
/// POURQUOI DEUX LIAISONS PLUTOT QU'UNE. Le fichier range ses reglages sous la section
/// « AssistantQR ». Les variables d'environnement, elles, portent deja le prefixe
/// <c>ASSISTANTQR_</c>, que le fournisseur retire : exiger en plus la section donnerait
/// <c>ASSISTANTQR_ASSISTANTQR__PROFILE</c>, que personne ne tapera jamais. On lie donc la
/// section pour le fichier, puis la racine pour l'environnement, sur le MEME objet — la
/// seconde liaison n'ecrase que les cles reellement presentes.
///
/// CE QUE CETTE CLASSE NE FAIT PAS : valider. Un topK negatif, un mode de filtrage mal
/// orthographie, un profil inconnu sont refuses plus loin, par
/// <c>PipelineOptions.Validate</c> et par le montage des dependances. La presentation
/// traduit du texte en reglages ; elle ne se prononce pas sur leur sens.
/// </remarks>
internal static class CliConfiguration
{
    /// <summary>Prefixe des variables d'environnement, retire par le fournisseur.</summary>
    public const string EnvironmentPrefix = "ASSISTANTQR_";

    /// <summary>Nom du fichier de configuration, copie a cote du binaire par le projet.</summary>
    public const string FileName = "appsettings.json";

    /// <summary>Options acceptees par toutes les commandes qui composent des dependances.</summary>
    public static readonly string[] OverrideOptions =
    {
        "profile", "embedding", "dimension", "chunking", "llm",
        "filter", "topk", "min-score", "temperature", "seed",
        "prompt", "prompt-version", "strict",
    };

    /// <summary>Charge la configuration et applique les surcharges de la ligne de commande.</summary>
    public static AssistantOptions Load(CommandLine commandLine)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile(FileName, optional: true, reloadOnChange: false)
            .AddEnvironmentVariables(EnvironmentPrefix)
            .Build();

        var options = new AssistantOptions();

        configuration.GetSection(AssistantOptions.SectionName).Bind(options);
        configuration.Bind(options);

        Override(options, commandLine);

        return options;
    }

    /// <summary>La configuration effective, telle qu'on veut la relire dans un terminal.</summary>
    public static IReadOnlyList<(string Label, string Value)> Describe(AssistantOptions options) => new[]
    {
        ("Profil", options.Profile),
        ("Corpus", PathResolver.Resolve(options.CorpusDirectory)),
        ("Gabarits", PathResolver.Resolve(options.PromptsDirectory)),
        ("Instantanes", PathResolver.Resolve(options.SnapshotsDirectory)),
        ("Decoupage", options.ChunkingStrategy),
        ("Embeddings", $"{options.Embeddings.Model} (dimension {Number(options.Embeddings.Dimension)}) " +
                       $"via {options.Embeddings.ServiceUrl}"),
        ("Modele de langue", $"{options.LanguageModel.Model} via {options.LanguageModel.OllamaUrl}"),
        ("Pipeline", $"topK={Number(options.Pipeline.TopK)} · minScore={Number(options.Pipeline.MinScore)} · " +
                     $"filtrage={options.Pipeline.AccessFilterMode} · T={Number(options.Pipeline.Temperature)} · " +
                     $"graine={options.Pipeline.Seed?.ToString(CultureInfo.InvariantCulture) ?? "(aucune)"}"),
        ("Gabarit", $"{options.Pipeline.PromptName}@{options.Pipeline.PromptVersion}"),
        ("Controle index/modele", options.Pipeline.StrictIndexModelCheck ? "strict" : "permissif (defaut)"),
    };

    /// <summary>
    /// Applique les options de ligne de commande sur les reglages deja charges. Chaque
    /// surcharge est celle d'un reglage qui change les reponses du systeme : elles
    /// finiront toutes dans l'empreinte de configuration d'un instantane, ce qui est la
    /// seule facon d'imputer une derive a l'invocation plutot qu'au modele.
    /// </summary>
    private static void Override(AssistantOptions options, CommandLine commandLine)
    {
        var profile = commandLine.Value("profile");
        if (profile is not null)
        {
            options.Profile = profile;
        }

        var embedding = commandLine.Value("embedding");
        if (embedding is not null)
        {
            options.Embeddings.Model = embedding;
        }

        var dimension = commandLine.Integer("dimension");
        if (dimension.HasValue)
        {
            options.Embeddings.Dimension = dimension.Value;
        }

        var chunking = commandLine.Value("chunking");
        if (chunking is not null)
        {
            options.ChunkingStrategy = chunking;
        }

        var languageModel = commandLine.Value("llm");
        if (languageModel is not null)
        {
            options.LanguageModel.Model = languageModel;
        }

        var filter = commandLine.Value("filter");
        if (filter is not null)
        {
            options.Pipeline.AccessFilterMode = filter;
        }

        var topK = commandLine.Integer("topk");
        if (topK.HasValue)
        {
            options.Pipeline.TopK = topK.Value;
        }

        var minScore = commandLine.Number("min-score");
        if (minScore.HasValue)
        {
            options.Pipeline.MinScore = minScore.Value;
        }

        var temperature = commandLine.Number("temperature");
        if (temperature.HasValue)
        {
            options.Pipeline.Temperature = temperature.Value;
        }

        var seed = commandLine.Integer("seed");
        if (seed.HasValue)
        {
            options.Pipeline.Seed = seed.Value;
        }

        var promptName = commandLine.Value("prompt");
        if (promptName is not null)
        {
            options.Pipeline.PromptName = promptName;
        }

        var promptVersion = commandLine.Value("prompt-version");
        if (promptVersion is not null)
        {
            options.Pipeline.PromptVersion = promptVersion;
        }

        if (commandLine.Has("strict"))
        {
            options.Pipeline.StrictIndexModelCheck = true;
        }
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
