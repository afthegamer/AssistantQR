using AssistantQR.Domain.Access;

namespace AssistantQR.Application.Model;

/// <summary>
/// DILEMME : l'existence même de <see cref="MaxAccessLevel"/> fait entrer une regle
/// metier (le controle d'acces) dans le contrat de l'index, donc jusque dans
/// l'infrastructure. C'est le prix du pre-filtrage. Voir README, section
/// « Le dilemme du filtrage ».
///
/// Le raisonnement, en deux temps :
/// — Sans ce type, l'index ne sait rien des habilitations, il rend les <c>topK</c>
///   meilleurs morceaux tous niveaux confondus, et l'Application ecarte ensuite ce
///   qui est interdit. Le Domain reste seul juge, mais on peut jeter les 4 resultats
///   sur 4 et repondre « rien de lisible » alors que le corpus contenait la reponse.
/// — Avec ce type, l'index filtre avant de classer, donc les <c>topK</c> sont tous
///   exploitables. On a gagne en pertinence et perdu en purete : une base vectorielle
///   connait desormais un concept metier, et un bogue dans SA comparaison de niveaux
///   devient une faille de securite hors de portee des tests du Domain.
/// Le projet garde volontairement les deux options ouvertes pour rendre l'arbitrage
/// mesurable au lieu de le trancher par principe.
/// </summary>
public abstract record SearchFilter
{
    private SearchFilter() { }

    /// <summary>Aucune contrainte : l'index classe tout le corpus.</summary>
    public sealed record None : SearchFilter;

    /// <summary>Ne rendre que les morceaux dont le niveau est lisible avec <paramref name="Level"/>.</summary>
    public sealed record MaxAccessLevel(AccessLevel Level) : SearchFilter;

    /// <summary>Instance partagee du filtre neutre.</summary>
    public static SearchFilter NoFilter { get; } = new None();

    /// <summary>Filtre par plafond d'habilitation.</summary>
    public static SearchFilter UpTo(AccessLevel level) => new MaxAccessLevel(level);
}
