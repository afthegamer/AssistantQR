using AssistantQR.Domain.Access;
using AssistantQR.Domain.Evidence;

namespace AssistantQR.Domain.Policies;

/// <summary>
/// REGLE METIER 3 : on ne s'appuie jamais sur un document qu'on n'a pas le droit de lire.
/// La regle est ici, en un seul endroit, et pas dans l'index vectoriel : l'index peut
/// pre-filtrer par efficacite, mais c'est cette fonction qui fait autorite. Un
/// pre-filtrage est une optimisation ; la politique reste la verite.
/// </summary>
public static class AccessPolicy
{
    /// <summary>Ce fragment est-il lisible par ce demandeur ?</summary>
    public static bool IsReadable(EvidenceFragment fragment, Requester requester) =>
        fragment.AccessLevel.IsReadableWith(requester.Clearance);

    /// <summary>Les fragments que le demandeur a le droit de lire, dans l'ordre d'entree.</summary>
    public static IReadOnlyList<EvidenceFragment> Readable(
        IReadOnlyList<EvidenceFragment> fragments, Requester requester) =>
        Filter(fragments, requester, keepReadable: true);

    /// <summary>Les fragments ecartes par l'habilitation. Utile pour expliquer un refus, jamais pour repondre.</summary>
    public static IReadOnlyList<EvidenceFragment> Forbidden(
        IReadOnlyList<EvidenceFragment> fragments, Requester requester) =>
        Filter(fragments, requester, keepReadable: false);

    private static IReadOnlyList<EvidenceFragment> Filter(
        IReadOnlyList<EvidenceFragment> fragments, Requester requester, bool keepReadable)
    {
        if (fragments is null || fragments.Count == 0)
        {
            return Array.Empty<EvidenceFragment>();
        }

        var result = new List<EvidenceFragment>(fragments.Count);
        foreach (var fragment in fragments)
        {
            if (IsReadable(fragment, requester) == keepReadable)
            {
                result.Add(fragment);
            }
        }

        return result;
    }
}
