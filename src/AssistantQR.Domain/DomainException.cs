namespace AssistantQR.Domain;

/// <summary>
/// Violation d'une invariante metier.
/// Le Domain leve SES PROPRES exceptions : il ne depend d'aucun type d'erreur
/// venu d'une bibliotheque technique. C'est la meme discipline que l'absence
/// de reference dans le .csproj, appliquee au vocabulaire des erreurs.
/// </summary>
public sealed class DomainException : Exception
{
    /// <summary>Construit l'exception avec un message lisible par un humain (en francais).</summary>
    public DomainException(string message) : base(message) { }
}
