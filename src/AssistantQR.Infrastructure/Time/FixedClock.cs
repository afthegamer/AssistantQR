using AssistantQR.Application.Ports;

namespace AssistantQR.Infrastructure.Time;

/// <summary>
/// Adaptateur FACTICE de <see cref="IClock"/> : une horloge arretee, que le test avance
/// lui-meme s'il en a besoin.
/// </summary>
/// <remarks>
/// C'est le double qui justifie a lui seul l'existence du port. Un instantane
/// d'evaluation porte une date de creation : avec l'horloge du systeme, deux
/// enregistrements du meme jeu de questions differeraient toujours d'au moins un champ,
/// et la comparaison — dont c'est l'unique raison d'etre — signalerait une derive
/// permanente qui n'en est pas une.
///
/// La propriete est modifiable pour permettre de simuler le passage du temps sans
/// reconstruire l'objet ni introduire un mecanisme d'avance automatique : le test
/// decide quand l'heure change, et de combien. Un double qui prendrait cette decision
/// tout seul reintroduirait exactement ce qu'on cherchait a supprimer.
/// </remarks>
public sealed class FixedClock : IClock
{
    /// <summary>Fige l'horloge sur l'instant fourni.</summary>
    public FixedClock(DateTimeOffset utcNow) => UtcNow = utcNow;

    /// <inheritdoc />
    /// <remarks>Modifiable : c'est le test qui fait avancer le temps, explicitement.</remarks>
    public DateTimeOffset UtcNow { get; set; }
}
