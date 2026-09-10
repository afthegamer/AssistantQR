using AssistantQR.Application.Ports;

namespace AssistantQR.Infrastructure.Time;

/// <summary>
/// Adaptateur REEL de <see cref="IClock"/> : l'horloge du systeme d'exploitation.
/// </summary>
/// <remarks>
/// Trois lignes utiles, et c'est tout l'interet. Cette classe est le SEUL endroit du
/// depot ou <c>DateTimeOffset.UtcNow</c> a le droit d'apparaitre. Le Domain et
/// l'Application n'y ont pas acces : un appel a l'horloge est une entree-sortie, et
/// les entrees-sorties ne franchissent la frontiere que par un port.
///
/// La regle vaut la peine d'etre enoncee ainsi, parce qu'elle est facile a
/// contourner sans y penser : <c>UtcNow</c> ne ressemble pas a un appel systeme, il
/// ressemble a une constante. C'est ce qui en fait une source de non-determinisme
/// particulierement discrete — et donc un bon candidat a l'interdiction explicite.
/// </remarks>
public sealed class SystemClock : IClock
{
    /// <summary>Instance partagee : la classe n'a aucun etat, en multiplier les copies n'apporte rien.</summary>
    public static SystemClock Instance { get; } = new();

    /// <inheritdoc />
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
