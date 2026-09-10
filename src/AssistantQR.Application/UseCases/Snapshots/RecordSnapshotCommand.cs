namespace AssistantQR.Application.UseCases.Snapshots;

/// <summary>
/// Nom de l'instantane a produire et jeu de questions a rejouer.
/// Le nom est une decision de l'operateur, pas une date generee : c'est lui qui donnera
/// son sens a la comparaison (« avant-changement-de-prompt » contre « apres »).
/// </summary>
public sealed record RecordSnapshotCommand(string Name, IReadOnlyList<QuestionSetItem> Questions);
