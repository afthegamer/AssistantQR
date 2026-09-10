using AssistantQR.Application.Configuration;
using AssistantQR.Application.Ports;

namespace AssistantQR.Application.Retrieval;

/// <summary>
/// Choisit la strategie de recuperation d'apres la configuration.
/// Une fabrique statique et non une injection de dependances : le mode de filtrage se
/// change d'une execution a l'autre — c'est même l'objet de la demonstration — alors
/// qu'un conteneur resout ses dependances une fois pour toutes au demarrage. Faire
/// dependre le cas d'usage d'un <c>IRetrievalStrategy</c> injecte figerait le mode
/// dans le cablage et empecherait de comparer les deux comportements dans un même
/// processus.
/// </summary>
public static class RetrievalStrategyFactory
{
    /// <summary>Instancie la strategie correspondant au mode demande.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Si le mode n'est pas reconnu.</exception>
    public static IRetrievalStrategy Create(AccessFilterMode mode, IVectorIndex index) => mode switch
    {
        AccessFilterMode.Pre => new PreFilterRetrievalStrategy(index),
        AccessFilterMode.Post => new PostFilterRetrievalStrategy(index),
        _ => throw new ArgumentOutOfRangeException(
            nameof(mode), mode, "Mode de filtrage des accès inconnu : attendu Pre ou Post."),
    };
}
