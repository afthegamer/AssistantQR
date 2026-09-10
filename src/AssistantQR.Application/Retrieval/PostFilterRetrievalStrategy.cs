using AssistantQR.Application.Configuration;
using AssistantQR.Application.Model;
using AssistantQR.Application.Ports;

using AssistantQR.Domain.Access;

namespace AssistantQR.Application.Retrieval;

/// <summary>
/// POST-FILTRAGE : l'index classe tout le corpus, l'Application ecarte ensuite ce que
/// le demandeur n'a pas le droit de lire.
/// L'index reste ignorant du metier — il ne connait ni habilitation ni niveau — et la
/// regle d'acces n'existe qu'a un seul endroit, dans le Domain. C'est la version
/// architecturalement propre, et c'est le defaut du projet.
/// Le cout est fonctionnel et parfaitement demontrable : si les quatre meilleurs
/// morceaux sont confidentiels, un agent au niveau interne recoit un refus
/// « rien de lisible » alors que la reponse existait, classee cinquieme. Augmenter
/// <c>TopK</c> attenue le probleme sans le supprimer — ce n'est pas une correction,
/// c'est un deplacement du seuil ou il se manifeste.
/// </summary>
public sealed class PostFilterRetrievalStrategy : IRetrievalStrategy
{
    private readonly IVectorIndex _index;

    /// <summary>Construit la strategie sur un index donne.</summary>
    public PostFilterRetrievalStrategy(IVectorIndex index) =>
        _index = index ?? throw new ArgumentNullException(nameof(index));

    /// <inheritdoc />
    public AccessFilterMode Mode => AccessFilterMode.Post;

    /// <inheritdoc />
    public async Task<RetrievalOutcome> RetrieveAsync(
        EmbeddingVector query,
        Requester requester,
        PipelineOptions options,
        CancellationToken cancellationToken = default)
    {
        // Aucune habilitation ne traverse la frontiere : l'index ne saura jamais qui demande.
        var results = await _index
            .SearchAsync(query, options.TopK, SearchFilter.NoFilter, cancellationToken)
            .ConfigureAwait(false);

        var fromIndex = RetrievalOrdering.SortByScoreDescending(results);

        var candidates = RetrievalOrdering.AboveThreshold(fromIndex, options.MinScore);

        // C'est ICI, et nulle part ailleurs, que le controle d'acces s'applique en mode post.
        var readable = RetrievalOrdering.KeepReadable(candidates, requester);

        return new RetrievalOutcome(fromIndex, readable);
    }
}
