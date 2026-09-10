using AssistantQR.Application.Ports;
using AssistantQR.Application.UseCases.AnswerQuestion;

using AssistantQR.Domain.Access;
using AssistantQR.Domain.Answers;
using AssistantQR.Domain.Questions;

namespace AssistantQR.Application.UseCases.Snapshots;

/// <summary>
/// Rejoue un jeu de questions et archive le comportement observe.
/// C'est le cas d'usage qui rend le systeme SURVEILLABLE. Un assistant fonde sur un
/// modele de langue n'a pas de suite de tests au sens habituel : on ne peut pas
/// affirmer a l'avance le texte exact d'une reponse. On peut en revanche figer le
/// comportement d'aujourd'hui, changer une chose, et mesurer ce qui a bouge. Ce
/// cas d'usage produit la reference ; <see cref="CompareSnapshotsUseCase"/> mesure.
/// </summary>
public sealed class RecordSnapshotUseCase
{
    private readonly AnswerQuestionUseCase _answerQuestion;
    private readonly ISnapshotStore _store;
    private readonly IClock _clock;

    /// <summary>
    /// Il depend d'un AUTRE cas d'usage, et non des ports que celui-ci utilise : rejouer
    /// un jeu de questions doit emprunter exactement le chemin de production, sinon
    /// l'instantane decrit un systeme qui n'existe pas.
    /// </summary>
    public RecordSnapshotUseCase(AnswerQuestionUseCase answerQuestion, ISnapshotStore store, IClock clock)
    {
        _answerQuestion = answerQuestion ?? throw new ArgumentNullException(nameof(answerQuestion));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>Execute toutes les questions du jeu, puis enregistre l'instantane.</summary>
    /// <exception cref="InvalidOperationException">Si le jeu de questions est vide.</exception>
    public async Task<EvaluationSnapshot> ExecuteAsync(
        RecordSnapshotCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.Questions is null || command.Questions.Count == 0)
        {
            throw new InvalidOperationException(
                "Un instantané sans question ne mesure rien : fournissez au moins une entrée dans le jeu d'évaluation.");
        }

        var entries = new List<SnapshotEntry>(command.Questions.Count);
        var configuration = ConfigurationFingerprint.Empty;

        for (var i = 0; i < command.Questions.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var item = command.Questions[i];

            // La validation se fait ici, a l'entree du domaine : une habilitation
            // inconnue dans le fichier de questions doit echouer bruyamment, pas se
            // traduire en un refus qu'on interpreterait comme un comportement normal.
            var question = Question.From(item.Question);
            var requester = Requester.Create(item.UserId, item.Clearance);

            var result = await _answerQuestion
                .ExecuteAsync(new AnswerQuestionCommand(question, requester), cancellationToken)
                .ConfigureAwait(false);

            if (i == 0)
            {
                // L'empreinte est prise sur la PREMIERE question. La configuration ne
                // change pas d'une question a l'autre au sein d'une même execution ;
                // la relire douze fois n'apporterait rien.
                configuration = result.Trace.Configuration;
            }

            entries.Add(BuildEntry(item, question, result));
        }

        var snapshot = new EvaluationSnapshot(
            command.Name,
            _clock.UtcNow,
            configuration,
            entries);

        await _store.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);

        return snapshot;
    }

    private static SnapshotEntry BuildEntry(
        QuestionSetItem item,
        Question question,
        AnswerQuestionResult result)
    {
        var answer = result.Outcome.AnswerOrNull;
        var refusal = result.Outcome as AnswerOutcome.Refused;

        var citedIds = new List<string>();
        if (answer is not null)
        {
            foreach (var documentId in answer.CitedDocumentIds)
            {
                citedIds.Add(documentId.Value);
            }
        }

        // Les scores des extraits reellement soumis au modele sont conserves : une
        // reponse identique obtenue avec des scores tres differents signale une derive
        // de la recherche que le seul texte ne revelerait pas.
        var scores = new List<double>(result.Trace.Supplied.Count);
        foreach (var scored in result.Trace.Supplied)
        {
            scores.Add(scored.Score);
        }

        return new SnapshotEntry(
            QuestionText: question.Text,
            UserId: item.UserId,
            Clearance: item.Clearance,
            Answered: result.Outcome.IsAnswered,
            AnswerText: answer?.Text,
            RefusalReason: refusal?.Reason.ToString(),
            CitedDocumentIds: citedIds,
            Scores: scores);
    }
}
