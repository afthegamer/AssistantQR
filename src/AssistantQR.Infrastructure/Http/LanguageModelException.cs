namespace AssistantQR.Infrastructure.Http;

/// <summary>Panne du modele de langue (Ollama, ou lecture d'un fichier de rejeu).</summary>
/// <remarks>
/// Le message porte par cette exception est un livrable en soi : il doit dire quoi taper
/// pour s'en sortir (« ollama pull granite4.2:3b », « bascule le profil sur offline »).
/// Un adaptateur qui se contente de relayer « 404 Not Found » a fait la moitie du travail.
/// </remarks>
public sealed class LanguageModelException : Exception
{
    public LanguageModelException(string message)
        : base(message)
    {
    }

    public LanguageModelException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
