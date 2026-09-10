using System.Globalization;

namespace AssistantQR.Application.Configuration;

/// <summary>
/// Tous les reglages qui changent le comportement du pipeline sans changer une seule
/// regle metier. Ils sont rassembles dans UN type, et non disperses en parametres de
/// methodes, pour une raison precise : c'est cet objet que l'on recopie dans
/// l'empreinte de configuration d'un instantane. Un reglage qui n'apparait pas ici
/// est un reglage dont on ne pourra pas prouver qu'il est responsable d'une derive.
/// </summary>
public sealed record PipelineOptions
{
    /// <summary>Nombre de morceaux demandes a l'index.</summary>
    public int TopK { get; init; } = 4;

    /// <summary>Seuil de similarite en dessous duquel un morceau est juge hors sujet.</summary>
    public double MinScore { get; init; } = 0.20;

    /// <summary>Temperature du modele. Zero par defaut : on veut la reproductibilite, pas le style.</summary>
    public double Temperature { get; init; }

    /// <summary>Graine transmise au modele quand il l'accepte.</summary>
    public int? Seed { get; init; } = 42;

    /// <summary>Longueur maximale de la completion.</summary>
    public int MaxTokens { get; init; } = 600;

    /// <summary>Nom du gabarit de prompt.</summary>
    public string PromptName { get; init; } = "answer-with-citations";

    /// <summary>Version du gabarit. Explicite : « la derniere » n'est pas reproductible.</summary>
    public string PromptVersion { get; init; } = "1.0.0";

    /// <summary>Pre ou post-filtrage des acces.</summary>
    public AccessFilterMode FilterMode { get; init; } = AccessFilterMode.Post;

    /// <summary>
    /// Si vrai, on refuse d'interroger un index construit avec un autre modele
    /// d'embeddings. Faux par defaut : c'est ce defaut qui rend la panne silencieuse
    /// (scenario B). Le defaut permissif est un choix pedagogique — en production, la
    /// valeur raisonnable est l'inverse.
    /// </summary>
    public bool StrictIndexModelCheck { get; init; }

    /// <summary>Longueur des extraits recopies dans les citations.</summary>
    public int ExcerptLength { get; init; } = 240;

    /// <summary>Reglages par defaut du projet.</summary>
    public static PipelineOptions Default { get; } = new();

    /// <summary>
    /// Verifie la coherence des reglages. On echoue au demarrage du cas d'usage plutot
    /// que de produire des resultats inexplicables : un <c>TopK</c> nul ne rend pas une
    /// erreur, il rend un refus « rien dans le corpus » parfaitement trompeur.
    /// </summary>
    /// <exception cref="InvalidOperationException">Si un reglage est hors de son domaine de validite.</exception>
    public void Validate()
    {
        if (TopK < 1)
        {
            throw new InvalidOperationException(
                $"TopK doit valoir au moins 1 : {TopK.ToString(CultureInfo.InvariantCulture)} demandé.");
        }

        if (MinScore is < 0 or > 1 || double.IsNaN(MinScore))
        {
            throw new InvalidOperationException(
                $"MinScore doit être compris entre 0 et 1 : {MinScore.ToString("0.###", CultureInfo.InvariantCulture)} demandé.");
        }

        if (Temperature < 0 || double.IsNaN(Temperature))
        {
            throw new InvalidOperationException(
                $"La température ne peut pas être négative : {Temperature.ToString("0.###", CultureInfo.InvariantCulture)} demandée.");
        }

        if (MaxTokens < 1)
        {
            throw new InvalidOperationException(
                $"MaxTokens doit valoir au moins 1 : {MaxTokens.ToString(CultureInfo.InvariantCulture)} demandé.");
        }
    }
}
