namespace AssistantQR.Infrastructure.Configuration;

/// <summary>
/// Les reglages du pipeline sous leur forme « fichier de configuration » : des types
/// simples, des accesseurs en ecriture, et le mode de filtrage ecrit en toutes lettres.
/// </summary>
/// <remarks>
/// POURQUOI CE DOUBLON APPARENT AVEC <c>PipelineOptions</c>. Un lieur de configuration
/// exige un constructeur sans parametre et des proprietes modifiables ; l'Application,
/// elle, veut un enregistrement immuable qu'on puisse recopier tel quel dans l'empreinte
/// d'un instantane sans craindre qu'il ait bouge entre-temps. Ces deux exigences sont
/// contradictoires : on ne peut pas les satisfaire avec un seul type. Le doublon est le
/// prix, la traduction se fait dans <see cref="AssistantOptions.ToPipelineOptions"/>.
///
/// Chacune de ces valeurs change les reponses du systeme sans qu'aucune regle metier ne
/// bouge. C'est pour cela qu'elles finissent, une par une, dans l'empreinte de
/// configuration des instantanes : une derive inexplicable est une derive dont on n'a
/// pas enregistre la cause.
/// </remarks>
public sealed class PipelineSection
{
    /// <summary>Nombre de morceaux demandes a l'index.</summary>
    public int TopK { get; set; } = 4;

    /// <summary>Seuil de similarite en dessous duquel un morceau est juge hors sujet.</summary>
    public double MinScore { get; set; } = 0.20;

    /// <summary>Temperature du modele. Zero par defaut : on veut la reproductibilite.</summary>
    public double Temperature { get; set; }

    /// <summary>Graine transmise au modele quand il l'accepte.</summary>
    public int? Seed { get; set; } = 42;

    /// <summary>Longueur maximale de la completion.</summary>
    public int MaxTokens { get; set; } = 600;

    /// <summary>Nom du gabarit de prompt.</summary>
    public string PromptName { get; set; } = "answer-with-citations";

    /// <summary>Version du gabarit. Explicite : « la derniere » n'est pas reproductible.</summary>
    public string PromptVersion { get; set; } = "1.0.0";

    /// <summary>« pre » ou « post » : quand le controle d'acces s'applique.</summary>
    public string AccessFilterMode { get; set; } = "post";

    /// <summary>
    /// Si vrai, on refuse d'interroger un index construit avec un autre modele
    /// d'embeddings. Faux par defaut : ce defaut permissif est ce qui rend la panne
    /// silencieuse du scenario B observable.
    /// </summary>
    public bool StrictIndexModelCheck { get; set; }

    /// <summary>Longueur des extraits recopies dans les citations.</summary>
    public int ExcerptLength { get; set; } = 240;
}
