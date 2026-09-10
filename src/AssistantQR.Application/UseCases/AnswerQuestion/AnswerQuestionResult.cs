using AssistantQR.Domain.Answers;

namespace AssistantQR.Application.UseCases.AnswerQuestion;

/// <summary>
/// La decision et son dossier.
/// Le cas d'usage ne rend jamais l'une sans l'autre : un refus sans trace est
/// inexplicable a l'usager, et une reponse sans trace est invérifiable par
/// l'exploitant. C'est le contrat minimal d'un systeme qui doit rendre des comptes.
/// </summary>
public sealed record AnswerQuestionResult(AnswerOutcome Outcome, RetrievalTrace Trace);
