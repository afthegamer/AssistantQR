using AssistantQR.Application.Configuration;
using AssistantQR.Application.Model;
using AssistantQR.Application.UseCases.Snapshots;

namespace AssistantQR.Application.UseCases.AnswerQuestion;

/// <summary>
/// Tout ce qui s'est passe pour produire une reponse.
/// Cette trace n'est pas de la journalisation : c'est un resultat de premiere classe,
/// rendu par le cas d'usage au même titre que la reponse. La raison tient en une
/// phrase — dans un systeme dont un maillon est probabiliste, « quelle est la
/// reponse ? » est une question moins utile que « d'ou vient cette reponse ? ».
///
/// <see cref="FromIndex"/> et <see cref="Supplied"/> sont conserves separement pour
/// rendre le controle d'acces mesurable : leur difference,
/// <see cref="DroppedByAccessFilter"/>, est exactement ce que le demandeur n'avait pas
/// le droit de voir. C'est cette liste qui rend le dilemme pre/post-filtrage visible
/// dans un terminal plutot que dans un debat.
/// </summary>
public sealed record RetrievalTrace(
    int CandidatesFromIndex,
    int CandidatesAfterAccessFilter,
    AccessFilterMode FilterMode,
    IReadOnlyList<ScoredFragment> FromIndex,
    IReadOnlyList<ScoredFragment> Supplied,
    IReadOnlyList<ScoredFragment> DroppedByAccessFilter,
    ConfigurationFingerprint Configuration,
    TimeSpan Duration,
    string? IndexModelWarning);
