using AssistantQR.Application.Model;

namespace AssistantQR.Application.Retrieval;

/// <summary>
/// Le resultat d'une recherche, en DEUX listes et pas une.
/// Ne rendre que la liste finale suffirait a repondre ; elle ne suffirait pas a
/// expliquer. La difference entre <see cref="FromIndex"/> et
/// <see cref="AfterAccessFilter"/> est exactement ce que le controle d'acces a
/// coute — le nombre de morceaux pertinents qu'un demandeur n'a pas le droit de
/// voir. C'est cette difference qui rend le dilemme pre/post-filtrage observable
/// dans la trace au lieu d'être un debat theorique.
/// </summary>
public sealed record RetrievalOutcome(
    IReadOnlyList<ScoredFragment> FromIndex,
    IReadOnlyList<ScoredFragment> AfterAccessFilter);
