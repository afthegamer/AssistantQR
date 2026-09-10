namespace AssistantQR.Application.Model;

/// <summary>
/// Ce que le modele a rendu : du texte brut, et l'identifiant du modele qui l'a produit.
/// Le texte n'est PAS une reponse — il ne le deviendra qu'apres analyse par
/// <see cref="ModelResponseParser"/> puis jugement par la politique du Domain.
/// Le decompte de jetons est facultatif : tous les fournisseurs ne le donnent pas,
/// et le pipeline ne doit pas en dependre.
/// </summary>
public sealed record LlmCompletion(
    string Text,
    string ModelId,
    int? PromptTokens = null,
    int? CompletionTokens = null);
