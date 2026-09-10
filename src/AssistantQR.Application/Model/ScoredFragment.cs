using AssistantQR.Domain.Evidence;

namespace AssistantQR.Application.Model;

/// <summary>
/// Un fragment du Domain accompagne de son score de similarite.
/// Le score ENVELOPPE le fragment au lieu d'être ajoute a
/// <see cref="EvidenceFragment"/> : c'est la technique qui vient se greffer sur le
/// metier, jamais l'inverse. Le Domain continue de raisonner sur des fragments nus,
/// et une regle metier ne peut donc pas se mettre a dependre d'un seuil de score.
/// </summary>
public sealed record ScoredFragment(EvidenceFragment Fragment, double Score);
