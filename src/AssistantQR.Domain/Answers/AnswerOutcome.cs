namespace AssistantQR.Domain.Answers;

/// <summary>
/// Hierarchie fermee : soit on repond, soit on refuse. Pas de troisieme cas.
/// Le constructeur prive interdit toute derivation hors de ce fichier, ce qui
/// rend le <c>switch</c> des appelants reellement exhaustif — un refus ne peut
/// pas être ajoute en douce ailleurs dans le depot.
/// </summary>
public abstract record AnswerOutcome
{
    private AnswerOutcome() { }

    /// <summary>Le systeme a produit une réponse citee.</summary>
    public sealed record Answered(Answer Answer) : AnswerOutcome;

    /// <summary>Le systeme a refuse, avec une cause et une explication en francais.</summary>
    public sealed record Refused(RefusalReason Reason, string Explanation) : AnswerOutcome;

    /// <summary>Raccourci de lecture.</summary>
    public bool IsAnswered => this is Answered;

    /// <summary>La réponse si elle existe, sinon <c>null</c>.</summary>
    public Answer? AnswerOrNull => (this as Answered)?.Answer;
}
