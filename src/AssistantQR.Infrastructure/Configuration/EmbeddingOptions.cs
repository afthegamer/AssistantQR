namespace AssistantQR.Infrastructure.Configuration;

/// <summary>
/// Reglages du service d'embeddings : quel modele, quelle dimension, ou le joindre.
/// </summary>
/// <remarks>
/// LE NOM ET LA DIMENSION VONT ENSEMBLE, ET C'EST UN PIEGE. Rien n'empeche de changer
/// <see cref="Model"/> sans toucher a <see cref="Dimension"/> : la configuration se
/// charge, le systeme demarre, l'index repond. La panne n'apparait qu'a la lecture des
/// reponses, qui deviennent subtilement hors sujet — c'est le scenario B. Deux garde-fous
/// existent ailleurs dans le depot : l'adaptateur HTTP refuse un vecteur dont la
/// dimension ne correspond pas a ce qui est declare ici, et le cas d'usage compare le
/// nom du modele a celui qui a construit l'index. Aucun des deux n'est ici, et ce n'est
/// pas un oubli : un objet de configuration ne doit rien verifier qu'il ne puisse
/// verifier seul.
/// </remarks>
public sealed class EmbeddingOptions
{
    /// <summary>Nom reserve de l'embedding factice, deterministe et hors-ligne.</summary>
    public const string HashingModel = "hashing-fake";

    /// <summary>
    /// Seconde variante du faux : MEME algorithme, MEME dimension, autre graine de
    /// projection.
    /// </summary>
    /// <remarks>
    /// C'EST LA LIGNE QUE LE COURS PROJETTE. Basculer <see cref="Model"/> de
    /// « hashing-fake » a « hashing-fake-b » revient a changer de modele d'embeddings :
    /// les vecteurs deviennent tout autres, la dimension ne bouge pas, rien ne plante,
    /// et une partie des reponses change. C'est la panne silencieuse du scenario B,
    /// reproductible hors ligne, sans installer quoi que ce soit.
    /// </remarks>
    public const string HashingModelB = "hashing-fake-b";

    /// <summary>« qwen3-embedding:0.6b », « bge-m3 », « hashing-fake » ou « hashing-fake-b ».</summary>
    public string Model { get; set; } = HashingModel;

    /// <summary>Dimension attendue des vecteurs. Doit correspondre au modele nomme ci-dessus.</summary>
    public int Dimension { get; set; } = 256;

    /// <summary>Adresse de base du service Python (embeddings ET index vectoriel).</summary>
    public string ServiceUrl { get; set; } = "http://localhost:8088";

    /// <summary>Delai d'attente. Genereux : le premier appel charge le modele en memoire.</summary>
    public int TimeoutSeconds { get; set; } = 120;
}
