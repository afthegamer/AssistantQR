namespace AssistantQR.Infrastructure.Http;

/// <summary>Panne du service d'embeddings distant.</summary>
/// <remarks>
/// POURQUOI UNE EXCEPTION PAR ADAPTATEUR — et pas une seule <c>InfrastructureException</c>
/// fourre-tout : quand la ligne de commande affiche le message, l'etudiant doit savoir
/// immediatement QUELLE piece est tombee. Le service Python d'embeddings, l'index
/// vectoriel et Ollama sont trois processus qu'on demarre separement ; les confondre
/// dans un seul type d'erreur reviendrait a rendre le diagnostic aussi vague que
/// « ca ne marche pas ».
///
/// Ces exceptions ne franchissent JAMAIS la frontiere vers l'Application : l'Application
/// ne connait que ses ports. Elles remontent jusqu'a la CLI, qui est deja du monde
/// exterieur et a le droit de savoir qu'il existe un service HTTP quelque part.
/// </remarks>
public sealed class EmbeddingServiceException : Exception
{
    public EmbeddingServiceException(string message)
        : base(message)
    {
    }

    public EmbeddingServiceException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
