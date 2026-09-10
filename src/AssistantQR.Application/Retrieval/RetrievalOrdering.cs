using AssistantQR.Application.Model;

using AssistantQR.Domain.Access;
using AssistantQR.Domain.Policies;

namespace AssistantQR.Application.Retrieval;

/// <summary>
/// Operations de tri et de filtrage partagees par les deux strategies.
/// Le type est <c>internal</c> : c'est un detail d'implementation de la recuperation,
/// pas une extension du contrat public de la couche. Une classe utilitaire publique
/// serait une invitation a court-circuiter les strategies depuis l'exterieur.
/// </summary>
internal static class RetrievalOrdering
{
    /// <summary>
    /// Tri par score decroissant. Le contrat de <c>IVectorIndex</c> promet deja ce tri ;
    /// on le refait quand même, parce qu'une garantie tenue par un adaptateur externe
    /// n'est pas une garantie.
    /// </summary>
    public static IReadOnlyList<ScoredFragment> SortByScoreDescending(IReadOnlyList<ScoredFragment>? results)
    {
        if (results is null || results.Count == 0)
        {
            return Array.Empty<ScoredFragment>();
        }

        var sorted = new List<ScoredFragment>(results);
        sorted.Sort(static (left, right) => right.Score.CompareTo(left.Score));
        return sorted;
    }

    /// <summary>Ecarte ce qui est sous le seuil de pertinence.</summary>
    public static IReadOnlyList<ScoredFragment> AboveThreshold(
        IReadOnlyList<ScoredFragment> results, double minScore)
    {
        var kept = new List<ScoredFragment>(results.Count);
        foreach (var scored in results)
        {
            if (scored.Score >= minScore)
            {
                kept.Add(scored);
            }
        }

        return kept;
    }

    /// <summary>
    /// Ne garde que les fragments lisibles. Le jugement est delegue a
    /// <see cref="AccessPolicy"/> : l'Application ne compare jamais deux niveaux
    /// d'habilitation elle-même, sinon la regle metier existerait a deux endroits.
    /// </summary>
    public static IReadOnlyList<ScoredFragment> KeepReadable(
        IReadOnlyList<ScoredFragment> results, Requester requester)
    {
        var kept = new List<ScoredFragment>(results.Count);
        foreach (var scored in results)
        {
            if (AccessPolicy.IsReadable(scored.Fragment, requester))
            {
                kept.Add(scored);
            }
        }

        return kept;
    }
}
