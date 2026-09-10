namespace AssistantQR.Application.UseCases.Snapshots;

/// <summary>
/// Une divergence sur une question. Les deux entrees comparees sont conservees, et pas
/// seulement leur verdict : un rapport de derive qui affiche « different » sans montrer
/// quoi oblige a rouvrir les deux fichiers a la main.
/// L'une des deux peut être nulle — c'est le cas d'une question ajoutee ou retiree du
/// jeu d'evaluation entre les deux enregistrements.
/// </summary>
public sealed record SnapshotDifference(
    string QuestionText,
    DifferenceKind Kind,
    SnapshotEntry? Baseline,
    SnapshotEntry? Candidate,
    string Summary);
