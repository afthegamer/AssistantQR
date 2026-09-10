using AssistantQR.Application.Configuration;

namespace AssistantQR.Infrastructure.Configuration;

/// <summary>
/// La configuration du depot telle qu'elle est ecrite dans un fichier ou dans des
/// variables d'environnement : des chaines, des nombres, des chemins.
/// </summary>
/// <remarks>
/// POURQUOI CE TYPE EXISTE ALORS QUE <see cref="PipelineOptions"/> EXISTE DEJA.
/// Les deux se ressemblent, et c'est justement ce qui rend la distinction interessante.
/// <see cref="PipelineOptions"/> vit en Application : c'est un enregistrement immuable,
/// type (une enumeration pour le mode de filtrage), valide, et qui finit tel quel dans
/// l'empreinte de configuration d'un instantane. <see cref="AssistantOptions"/> vit en
/// Infrastructure : il a des accesseurs en ecriture et un constructeur sans parametre
/// parce qu'un lieur de configuration en a besoin, il transporte des chemins et des URL
/// dont l'Application n'a jamais entendu parler, et il accepte « post » sous forme de
/// chaine — la forme que peut ecrire un humain dans un fichier.
///
/// La conversion se fait ici, dans <see cref="ToPipelineOptions"/>, et nulle part
/// ailleurs. C'est exactement le meme geste que la traduction « interne » vers
/// <c>AccessLevel.Internal</c> a la lecture du corpus : le vocabulaire du monde
/// exterieur s'arrete a la frontiere. L'Application ne doit connaitre ni le nom d'une
/// section de configuration, ni l'orthographe qu'on a choisie pour un mode de filtrage.
///
/// Le prix de cette separation est une recopie champ a champ, un peu ingrate. Le
/// benefice est qu'aucun lieur de configuration, aucun format de fichier et aucune
/// bibliotheque de configuration n'apparait jamais dans une couche interieure.
/// </remarks>
public sealed class AssistantOptions
{
    /// <summary>Nom de la section de configuration qui porte ces reglages.</summary>
    public const string SectionName = "AssistantQR";

    /// <summary>Valeur du profil entierement hors-ligne.</summary>
    public const string OfflineProfile = "offline";

    /// <summary>Valeur du profil qui parle aux services locaux (Ollama, service Python).</summary>
    public const string LocalProfile = "local";

    /// <summary>« offline » (tout factice, zero reseau) ou « local » (Ollama + service Python).</summary>
    public string Profile { get; set; } = OfflineProfile;

    /// <summary>
    /// Autorise une doublure a rester branchee alors que le profil est « local ».
    /// Faux par defaut, et il faut que ca le reste.
    /// </summary>
    /// <remarks>
    /// Sans ce reglage, un nom de doublure oublie dans la configuration donnait la
    /// doublure en profil « local », sans un mot : on croyait mesurer un vrai modele,
    /// on mesurait trente lignes de hachage. Le systeme repondait, les scores etaient
    /// plausibles, rien n'echouait — c'est la panne muette que ce depot enseigne, et
    /// elle etait dans le depot.
    ///
    /// Le reglage existe quand meme, parce que le cas est legitime : essayer le vrai
    /// modele de langue sans demarrer le service Python, par exemple. Mais il doit etre
    /// DEMANDE. Une doublure admise volontairement n'est plus une panne muette, c'est
    /// une decision — et elle apparait dans l'empreinte de configuration des instantanes.
    /// </remarks>
    public bool AllowFakeAdapters { get; set; }

    /// <summary>Dossier du corpus, relatif a la racine du depot ou absolu.</summary>
    public string CorpusDirectory { get; set; } = "corpus";

    /// <summary>Dossier des gabarits de prompt.</summary>
    public string PromptsDirectory { get; set; } = "prompts";

    /// <summary>Dossier des instantanes d'evaluation.</summary>
    public string SnapshotsDirectory { get; set; } = "snapshots";

    /// <summary>Identifiant de la strategie de decoupage (voir <c>ChunkingStrategyFactory</c>).</summary>
    public string ChunkingStrategy { get; set; } = "paragraph";

    /// <summary>Reglages du service d'embeddings.</summary>
    public EmbeddingOptions Embeddings { get; set; } = new();

    /// <summary>Reglages du modele de langue.</summary>
    public LanguageModelOptions LanguageModel { get; set; } = new();

    /// <summary>Reglages du pipeline, sous leur forme « fichier de configuration ».</summary>
    public PipelineSection Pipeline { get; set; } = new();

    /// <summary>
    /// Vrai si le profil interdit tout appel reseau. Ce booleen commande le choix des
    /// adaptateurs : c'est la seule question que le montage pose au profil.
    /// </summary>
    public bool IsOffline =>
        string.Equals(Profile?.Trim(), OfflineProfile, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Traduit la section de configuration en reglages d'Application. La validation des
    /// bornes reste du cote de <see cref="PipelineOptions.Validate"/> : elle est metier,
    /// pas syntaxique. Ce qui se joue ici est uniquement la traduction du vocabulaire.
    /// </summary>
    /// <exception cref="InvalidOperationException">Si le mode de filtrage est inconnu.</exception>
    public PipelineOptions ToPipelineOptions() => new()
    {
        TopK = Pipeline.TopK,
        MinScore = Pipeline.MinScore,
        Temperature = Pipeline.Temperature,
        Seed = Pipeline.Seed,
        MaxTokens = Pipeline.MaxTokens,
        PromptName = Pipeline.PromptName,
        PromptVersion = Pipeline.PromptVersion,
        FilterMode = ParseFilterMode(Pipeline.AccessFilterMode),
        StrictIndexModelCheck = Pipeline.StrictIndexModelCheck,
        ExcerptLength = Pipeline.ExcerptLength,
    };

    /// <summary>
    /// Une faute de frappe dans « post » ne doit pas se traduire par un basculement
    /// silencieux vers l'autre mode : les deux modes ne donnent pas les memes reponses,
    /// et l'ecart serait attribue au modele plutot qu'a la configuration.
    /// </summary>
    private static AccessFilterMode ParseFilterMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "pre" => AccessFilterMode.Pre,
        "post" => AccessFilterMode.Post,
        _ => throw new InvalidOperationException(
            $"Mode de filtrage des accès inconnu : « {value} ». Valeurs acceptées : « pre » ou « post »."),
    };
}
