namespace AssistantQR.Application.UseCases.Snapshots;

/// <summary>
/// Le rapport de comparaison entre un instantane de reference et un candidat.
/// Il met deliberement cote a cote deux choses : ce qui a change dans les REPONSES et
/// ce qui a change dans la CONFIGURATION. Lues separement, la premiere liste inquiete
/// et la seconde ennuie ; lues ensemble, elles forment une explication. Une derive de
/// 40 % assortie d'un changement de version de prompt est un resultat attendu ; la
/// même derive sans aucune difference de configuration est une alerte.
/// </summary>
public sealed record SnapshotComparison(
    string BaselineName,
    string CandidateName,
    ConfigurationFingerprint BaselineConfiguration,
    ConfigurationFingerprint CandidateConfiguration,
    IReadOnlyList<string> ConfigurationDifferences,
    IReadOnlyList<SnapshotDifference> Differences)
{
    /// <summary>Nombre de questions comparees.</summary>
    public int TotalCount => Differences.Count;

    /// <summary>Nombre de questions dont le resultat a bouge.</summary>
    public int ChangedCount
    {
        get
        {
            var count = 0;
            foreach (var difference in Differences)
            {
                if (difference.Kind != DifferenceKind.Identical)
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>
    /// Proportion de questions ayant change, entre 0 et 1. Zero si le rapport est vide :
    /// aucune comparaison ne signifie aucune derive constatee, pas une derive totale.
    /// </summary>
    public double DriftRatio => TotalCount == 0 ? 0 : (double)ChangedCount / TotalCount;
}
