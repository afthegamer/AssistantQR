namespace AssistantQR.Application.UseCases.Snapshots;

/// <summary>
/// Le comportement complet du systeme sur un jeu de questions, a un instant donne et
/// sous une configuration donnee.
/// C'est la reponse a une impasse methodologique : un composant non deterministe ne se
/// valide pas par assertion exacte. On ne peut pas ecrire « la reponse doit être ceci ».
/// On peut en revanche ecrire « la reponse ne doit pas avoir change depuis qu'on l'a
/// relue et acceptee » — c'est le principe des tests d'approbation, transpose a un
/// systeme a base de modele de langue.
/// La configuration est enregistree AVEC les resultats : un instantane sans son
/// contexte de production ne prouve rien.
/// </summary>
public sealed record EvaluationSnapshot(
    string Name,
    DateTimeOffset CreatedAt,
    ConfigurationFingerprint Configuration,
    IReadOnlyList<SnapshotEntry> Entries);
