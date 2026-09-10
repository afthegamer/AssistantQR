using System.Text;

using AssistantQR.Application.Ports;

namespace AssistantQR.Application.UseCases.Snapshots;

/// <summary>
/// Compare deux instantanes et qualifie chaque divergence.
/// La comparaison ne dit pas si le systeme s'est ameliore — aucune machine ne peut le
/// dire ici. Elle dit CE QUI a change et A QUEL POINT, en separant les derives benignes
/// (une reformulation) des changements de comportement (un refus devenu reponse).
/// C'est un instrument de mesure, pas un juge : la lecture reste humaine.
/// </summary>
public sealed class CompareSnapshotsUseCase
{
    private readonly ISnapshotStore _store;

    /// <summary>Un seul port : la comparaison elle-même est un calcul pur.</summary>
    public CompareSnapshotsUseCase(ISnapshotStore store) =>
        _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>Charge les deux instantanes et produit le rapport.</summary>
    /// <exception cref="InvalidOperationException">Si l'un des deux instantanes est introuvable.</exception>
    public async Task<SnapshotComparison> ExecuteAsync(
        string baselineName,
        string candidateName,
        CancellationToken cancellationToken = default)
    {
        var baseline = await _store.LoadAsync(baselineName, cancellationToken).ConfigureAwait(false)
                       ?? throw new InvalidOperationException(
                           $"L'instantané de référence « {baselineName} » est introuvable.");

        var candidate = await _store.LoadAsync(candidateName, cancellationToken).ConfigureAwait(false)
                        ?? throw new InvalidOperationException(
                            $"L'instantané candidat « {candidateName} » est introuvable.");

        // Appariement par (question, demandeur) : la même question posee par un visiteur
        // et par un agent sont deux cas distincts, les confondre melangerait deux
        // comportements attendus differents.
        var candidateByKey = IndexByKey(candidate.Entries);
        var baselineByKey = IndexByKey(baseline.Entries);

        var differences = new List<SnapshotDifference>();

        foreach (var entry in baseline.Entries)
        {
            var key = KeyOf(entry);
            if (candidateByKey.TryGetValue(key, out var other))
            {
                differences.Add(Compare(entry, other));
            }
            else
            {
                differences.Add(new SnapshotDifference(
                    entry.QuestionText,
                    DifferenceKind.MissingInCandidate,
                    entry,
                    null,
                    $"La question n'a pas été rejouée dans « {candidateName} » pour l'utilisateur « {entry.UserId} »."));
            }
        }

        foreach (var entry in candidate.Entries)
        {
            if (!baselineByKey.ContainsKey(KeyOf(entry)))
            {
                differences.Add(new SnapshotDifference(
                    entry.QuestionText,
                    DifferenceKind.MissingInBaseline,
                    null,
                    entry,
                    $"La question est nouvelle : absente de « {baselineName} » pour l'utilisateur « {entry.UserId} »."));
            }
        }

        return new SnapshotComparison(
            BaselineName: baseline.Name,
            CandidateName: candidate.Name,
            BaselineConfiguration: baseline.Configuration,
            CandidateConfiguration: candidate.Configuration,
            ConfigurationDifferences: baseline.Configuration.DifferencesWith(candidate.Configuration),
            Differences: differences);
    }

    /// <summary>
    /// Qualification d'une divergence, du plus grave au plus benin. L'ordre n'est pas
    /// arbitraire : une question qui passe du refus a la reponse a forcement des
    /// citations et un texte differents, et la signaler comme « texte modifie »
    /// enterrerait l'information importante sous la plus anodine.
    /// </summary>
    private static SnapshotDifference Compare(SnapshotEntry baseline, SnapshotEntry candidate)
    {
        if (baseline.Answered != candidate.Answered ||
            !string.Equals(baseline.RefusalReason, candidate.RefusalReason, StringComparison.Ordinal))
        {
            return new SnapshotDifference(
                baseline.QuestionText,
                DifferenceKind.RefusalChanged,
                baseline,
                candidate,
                $"Décision modifiée : {DescribeDecision(baseline)} → {DescribeDecision(candidate)}.");
        }

        if (!SameSequence(baseline.CitedDocumentIds, candidate.CitedDocumentIds))
        {
            return new SnapshotDifference(
                baseline.QuestionText,
                DifferenceKind.CitationsChanged,
                baseline,
                candidate,
                $"Sources modifiées : [{string.Join(", ", baseline.CitedDocumentIds)}] → " +
                $"[{string.Join(", ", candidate.CitedDocumentIds)}].");
        }

        if (!string.Equals(Normalize(baseline.AnswerText), Normalize(candidate.AnswerText), StringComparison.Ordinal))
        {
            return new SnapshotDifference(
                baseline.QuestionText,
                DifferenceKind.AnswerTextChanged,
                baseline,
                candidate,
                "Mêmes sources et même décision, texte reformulé.");
        }

        return new SnapshotDifference(
            baseline.QuestionText,
            DifferenceKind.Identical,
            baseline,
            candidate,
            "Aucun changement.");
    }

    private static string DescribeDecision(SnapshotEntry entry) =>
        entry.Answered ? "réponse" : $"refus ({entry.RefusalReason ?? "motif inconnu"})";

    private static Dictionary<(string Question, string UserId), SnapshotEntry> IndexByKey(
        IReadOnlyList<SnapshotEntry> entries)
    {
        var map = new Dictionary<(string Question, string UserId), SnapshotEntry>();
        foreach (var entry in entries)
        {
            // Un doublon dans un jeu de questions est une erreur de saisie ; on garde la
            // premiere occurrence plutot que de faire echouer toute la comparaison.
            map.TryAdd(KeyOf(entry), entry);
        }

        return map;
    }

    private static (string Question, string UserId) KeyOf(SnapshotEntry entry) =>
        (entry.QuestionText, entry.UserId);

    private static bool SameSequence(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i], right[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Normalise les blancs avant comparaison : un retour a la ligne de plus dans la
    /// sortie du modele n'est pas une derive de comportement, et le signaler comme telle
    /// noierait les vraies differences.
    /// </summary>
    private static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        var previousWasBlank = false;

        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                previousWasBlank = true;
                continue;
            }

            if (previousWasBlank && builder.Length > 0)
            {
                builder.Append(' ');
            }

            previousWasBlank = false;
            builder.Append(c);
        }

        return builder.ToString();
    }
}
