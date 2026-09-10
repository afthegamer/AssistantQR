using AssistantQR.Domain.Access;
using AssistantQR.Domain.Documents;

namespace AssistantQR.Domain.Evidence;

/// <summary>
/// Un extrait de document susceptible d'appuyer une réponse.
/// NOTE PEDAGOGIQUE : ce type ne porte PAS de score de similarite. Le score est un
/// artefact de la mecanique de recherche (donc Application), pas un concept metier.
/// Le fragment transporte son propre niveau d'accès : la politique d'accès doit
/// pouvoir trancher sans aller rechercher le document d'origine.
/// </summary>
public sealed record EvidenceFragment(
    DocumentId DocumentId,
    string DocumentTitle,
    string Text,
    AccessLevel AccessLevel,
    int Ordinal);
