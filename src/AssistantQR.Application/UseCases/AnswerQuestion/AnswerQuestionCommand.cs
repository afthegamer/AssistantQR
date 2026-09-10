using AssistantQR.Domain.Access;
using AssistantQR.Domain.Questions;

namespace AssistantQR.Application.UseCases.AnswerQuestion;

/// <summary>
/// Ce qu'il faut pour poser une question : le texte et l'identite de celui qui demande.
/// Les deux champs sont des types du Domain deja valides. La couche de presentation
/// convertit ses chaines en <see cref="Question"/> et <see cref="Requester"/> AVANT
/// d'entrer dans le cas d'usage : un texte vide ou un niveau d'habilitation inconnu
/// echoue a la frontiere, pas au milieu du pipeline apres deux appels reseau.
/// </summary>
public sealed record AnswerQuestionCommand(Question Question, Requester Requester);
