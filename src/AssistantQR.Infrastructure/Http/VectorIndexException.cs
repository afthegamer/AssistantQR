namespace AssistantQR.Infrastructure.Http;

/// <summary>Panne de l'index vectoriel distant.</summary>
/// <remarks>
/// L'index est le composant qui echoue le plus silencieusement : il repond « 200 OK »
/// avec des resultats absurdes quand le vecteur de requete vient d'un autre modele que
/// celui qui a construit l'index. Cette exception ne couvre donc QUE les pannes franches
/// (service injoignable, corps illisible, niveau d'acces inconnu) — jamais l'incoherence
/// modele/index, qui est detectee en Application a partir des metadonnees.
/// </remarks>
public sealed class VectorIndexException : Exception
{
    public VectorIndexException(string message)
        : base(message)
    {
    }

    public VectorIndexException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
