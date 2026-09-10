using AssistantQR.Application.Configuration;
using AssistantQR.Application.Model;

using AssistantQR.Domain.Access;

namespace AssistantQR.Application.Retrieval;

/// <summary>Comment on va chercher les extraits, et a quel moment on applique les droits.</summary>
/// <remarks>
/// CE N'EST PAS UN PORT — et le contraste avec le dossier <c>Ports/</c> est le point
/// de la lecon.
///
/// Un port designe une frontiere avec le monde exterieur : derriere
/// <c>IVectorIndex</c> il y a un processus Python, derriere <c>ILanguageModel</c> un
/// serveur Ollama, derriere <c>IClock</c> le systeme d'exploitation. On les inverse
/// parce que sans cela la couche dependrait de ces mondes-la.
///
/// Ici, rien de tel. Les deux implementations vivent dans l'Application, ne parlent
/// qu'a <c>IVectorIndex</c> — un port, lui — et n'ont aucune dependance a inverser.
/// L'interface n'existe que pour offrir deux algorithmes interchangeables : c'est le
/// patron Strategie, pas le patron Ports et Adaptateurs. Aucun adaptateur
/// d'infrastructure n'implementera jamais ce contrat.
///
/// LE TEST QUI TRANCHE : « si je supprime cette interface, est-ce que la couche se met
/// a dependre d'une technologie ? » Pour <c>IEmbeddingService</c>, oui — l'Application
/// devrait embarquer un client HTTP. Pour <c>IRetrievalStrategy</c>, non : il resterait
/// une methode avec un <c>switch</c>. Une interface qui echoue a ce test est un confort
/// d'organisation, pas une frontiere architecturale. Les confondre est la faute la plus
/// repandue dans les projets qui « font de la Clean Architecture » : on finit avec
/// quarante interfaces dont trois seulement protegent quelque chose.
/// </remarks>
public interface IRetrievalStrategy
{
    /// <summary>Le moment ou cette strategie applique le controle d'acces.</summary>
    AccessFilterMode Mode { get; }

    /// <summary>Cherche les extraits pertinents et lisibles pour ce demandeur.</summary>
    Task<RetrievalOutcome> RetrieveAsync(
        EmbeddingVector query,
        Requester requester,
        PipelineOptions options,
        CancellationToken cancellationToken = default);
}
