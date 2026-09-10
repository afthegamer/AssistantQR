using AssistantQR.Application.Configuration;
using AssistantQR.Application.Model;
using AssistantQR.Application.Ports;

using AssistantQR.Domain.Access;
using AssistantQR.Domain.Policies;

namespace AssistantQR.Application.Retrieval;

/// <summary>
/// PRE-FILTRAGE : l'index ne classe que ce que le demandeur a le droit de lire.
/// On obtient <c>topK</c> resultats tous exploitables — un agent qui interroge le
/// systeme ne « perd » plus ses places de classement au profit de documents
/// confidentiels qu'il ne verra jamais.
/// Le cout est architectural : l'habilitation traverse la frontiere et devient une
/// contrainte que l'index doit honorer. La securite du systeme repose desormais en
/// partie sur la justesse d'un composant que les tests du Domain n'atteignent pas.
/// </summary>
public sealed class PreFilterRetrievalStrategy : IRetrievalStrategy
{
    private readonly IVectorIndex _index;

    /// <summary>Construit la strategie sur un index donne.</summary>
    public PreFilterRetrievalStrategy(IVectorIndex index) =>
        _index = index ?? throw new ArgumentNullException(nameof(index));

    /// <inheritdoc />
    public AccessFilterMode Mode => AccessFilterMode.Pre;

    /// <inheritdoc />
    public async Task<RetrievalOutcome> RetrieveAsync(
        EmbeddingVector query,
        Requester requester,
        PipelineOptions options,
        CancellationToken cancellationToken = default)
    {
        // L'habilitation part avec la requete : c'est la l'essence du pre-filtrage.
        var results = await _index
            .SearchAsync(query, options.TopK, SearchFilter.UpTo(requester.Clearance), cancellationToken)
            .ConfigureAwait(false);

        var fromIndex = RetrievalOrdering.SortByScoreDescending(results);

        // Le seuil de pertinence est applique en Application, jamais par l'index :
        // c'est un reglage de pipeline, il doit rester visible dans PipelineOptions.
        var candidates = RetrievalOrdering.AboveThreshold(fromIndex, options.MinScore);

        // DEFENSE EN PROFONDEUR. L'index affirme avoir filtre ; la politique du Domain
        // le verifie quand même. Cette passe ne doit RIEN retirer — si elle retire
        // quelque chose, l'adaptateur d'index est defaillant, et c'est exactement le
        // genre de faute qu'un pre-filtrage rend possible. Le controle coute quelques
        // comparaisons d'entiers ; la faille qu'il rattrape coute bien plus.
        var readable = RetrievalOrdering.KeepReadable(candidates, requester);

        return new RetrievalOutcome(fromIndex, readable);
    }
}
