using AssistantQR.Application.Ports;

namespace AssistantQR.Application.Tests.Doubles;

/// <summary>
/// Horloge immobile.
///
/// Sans elle, deux instantanes du meme jeu de questions differeraient toujours par
/// leur date de creation, et la comparaison — dont c'est le seul objet — deviendrait
/// inutilisable en test. C'est la demonstration la plus courte de l'interet d'un port :
/// une interface d'une seule propriete suffit a rendre reproductible ce qui ne l'etait
/// pas, alors qu'un appel a <c>DateTimeOffset.UtcNow</c> ecrit en dur au coeur du
/// pipeline aurait ete indeboulonnable.
/// </summary>
public sealed class FixedClock : IClock
{
    /// <summary>Instant conventionnel utilise par defaut dans les tests.</summary>
    public static readonly DateTimeOffset DefaultInstant = new(2026, 3, 14, 9, 26, 53, TimeSpan.Zero);

    /// <summary>Construit une horloge arretee sur l'instant fourni.</summary>
    public FixedClock(DateTimeOffset? utcNow = null) => UtcNow = utcNow ?? DefaultInstant;

    /// <summary>L'instant rendu. Modifiable pour simuler le passage du temps entre deux instantanes.</summary>
    public DateTimeOffset UtcNow { get; set; }
}
