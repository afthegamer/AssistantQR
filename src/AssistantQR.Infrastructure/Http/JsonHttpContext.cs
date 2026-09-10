namespace AssistantQR.Infrastructure.Http;

/// <summary>
/// Ce que <see cref="JsonHttp"/> doit savoir pour transformer une panne HTTP en phrase
/// francaise utile : de quel service on parle, quoi faire quand il ne repond pas, et
/// quel type d'exception fabriquer.
/// </summary>
/// <remarks>
/// Ce petit record evite la signature a huit parametres que reclamerait sinon chaque
/// appel. Chaque adaptateur en construit un seul, une fois, dans son constructeur : sa
/// « carte de visite » aupres de la plomberie commune.
/// </remarks>
internal sealed record JsonHttpContext(
    string ServiceLabel,
    string UnreachableRemedy,
    Func<string, Exception?, Exception> CreateException)
{
    /// <summary>
    /// Message specialise pour un statut HTTP donne (statut, code d'erreur du corps).
    /// Renvoyer <c>null</c> laisse la plomberie composer le message generique.
    /// C'est par ce crochet qu'Ollama traduit son 404 en « le modele n'est pas installe ».
    /// </summary>
    public Func<int, string?, string?>? DescribeStatus { get; init; }
}
