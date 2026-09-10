namespace AssistantQR.Infrastructure.Configuration;

/// <summary>
/// Reglages du modele de langue : lequel, ou, et pendant combien de temps.
/// </summary>
/// <remarks>
/// C'EST LA LIGNE QUE LE COURS FAIT VARIER EN DIRECT. Passer <see cref="Model"/> de
/// « extractive-fake » a « granite4.2:3b » remplace un extracteur deterministe de
/// trente lignes par un modele de plusieurs milliards de parametres, sans qu'aucun
/// cas d'usage, aucune politique metier ni aucun test du Domain ne change d'une
/// virgule. C'est la demonstration du scenario A ; le montage qui la rend possible
/// est dans <c>ServiceCollectionExtensions</c>.
///
/// Le nom porte donc deux roles a la fois : il designe un modele reel cote Ollama, et
/// il sert de nom reserve pour les deux faux (« extractive-fake », « replay »). Cette
/// ambiguite est assumee — elle evite un second reglage « type d'adaptateur » que
/// personne ne penserait a changer en meme temps que le nom du modele.
/// </remarks>
public sealed class LanguageModelOptions
{
    /// <summary>Nom reserve du faux extractif, deterministe et hors-ligne.</summary>
    public const string ExtractiveModel = "extractive-fake";

    /// <summary>Nom reserve du faux de rejeu, alimente par un fichier JSON.</summary>
    public const string ReplayModel = "replay";

    /// <summary>« extractive-fake », « replay », ou le nom d'un modele Ollama (« granite4.2:3b »).</summary>
    public string Model { get; set; } = ExtractiveModel;

    /// <summary>Adresse de base du serveur Ollama.</summary>
    public string OllamaUrl { get; set; } = "http://localhost:11434";

    /// <summary>Delai d'attente. Une generation sur processeur peut etre longue.</summary>
    public int TimeoutSeconds { get; set; } = 180;

    /// <summary>Fichier de rejeu, utilise seulement quand <see cref="Model"/> vaut « replay ».</summary>
    public string ReplayFile { get; set; } = "snapshots/replay-llm.json";
}
