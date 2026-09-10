namespace AssistantQR.Application.UseCases.Snapshots;

/// <summary>
/// Une entree du jeu de questions d'evaluation : la question ET qui la pose.
/// L'habilitation fait partie du cas de test, elle n'est pas un parametre d'execution :
/// « quels sont les horaires ? » posee par un visiteur et par un agent sont deux cas
/// distincts qui doivent donner deux resultats distincts. Un jeu de questions qui
/// oublierait le demandeur ne pourrait pas tester le controle d'acces.
/// Les champs sont des chaines et non des types du Domain : cet objet vient d'un
/// fichier JSON exterieur, la validation se fera a la construction du
/// <c>Requester</c> — au moment ou une donnee invalide doit echouer.
/// </summary>
public sealed record QuestionSetItem(string Question, string UserId, string Clearance);
