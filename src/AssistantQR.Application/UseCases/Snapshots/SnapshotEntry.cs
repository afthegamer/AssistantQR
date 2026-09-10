namespace AssistantQR.Application.UseCases.Snapshots;

/// <summary>
/// Le resultat enregistre pour une question. C'est une PHOTOGRAPHIE, volontairement
/// aplatie en types primitifs : un instantane doit rester relisible dans dix ans, avec
/// un editeur de texte, même si les types du Domain ont change entre-temps. Y ranger
/// un <c>AnswerOutcome</c> lierait le format d'archive a la forme actuelle du code.
/// <c>Answered</c> et <c>RefusalReason</c> sont conserves separement du texte parce que
/// la comparaison d'instantanes commence par la : passer d'une reponse a un refus est
/// une derive d'une autre nature qu'une reformulation.
/// </summary>
public sealed record SnapshotEntry(
    string QuestionText,
    string UserId,
    string Clearance,
    bool Answered,
    string? AnswerText,
    string? RefusalReason,
    IReadOnlyList<string> CitedDocumentIds,
    IReadOnlyList<double> Scores);
