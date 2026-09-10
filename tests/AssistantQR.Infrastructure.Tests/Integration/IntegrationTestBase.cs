using Xunit.Abstractions;

namespace AssistantQR.Infrastructure.Tests.Integration;

/// <summary>
/// Socle des tests qui exigent un service reel (Ollama, service Python).
/// </summary>
/// <remarks>
/// POURQUOI CES TESTS NE SONT PAS DES TESTS COMME LES AUTRES. Tout le reste de ce projet
/// tourne hors ligne et rend le meme verdict sur n'importe quelle machine. Ceux-ci
/// dependent d'un processus tiers, de sa version, du modele installe, et de la memoire
/// disponible. Les melanger au reste transformerait une suite deterministe en oracle
/// capricieux — et une suite capricieuse finit toujours par etre ignoree, y compris
/// quand elle a raison.
///
/// POURQUOI CE MECANISME DE SAUT PLUTOT QUE <c>[Fact(Skip = ...)]</c>. L'argument d'un
/// attribut doit etre une constante de compilation ; une variable d'environnement n'en
/// est pas une, et xUnit 2 ne connait pas le saut dynamique. Ajouter un paquet pour cela
/// serait payer une dependance pour trois lignes. On retourne donc tot, en ecrivant la
/// raison dans la sortie du test : le test compte comme reussi, ce qui est honnete —
/// il n'a rien constate, il n'a donc rien a reprocher.
///
/// Le declencheur est unique et explicite : <c>ASSISTANTQR_INTEGRATION=1</c>.
/// </remarks>
public abstract class IntegrationTestBase
{
    /// <summary>Valeur du trait porte par tous les tests de ce dossier.</summary>
    public const string CategoryName = "Integration";

    /// <summary>Variable d'environnement qui autorise les appels reseau.</summary>
    private const string SwitchName = "ASSISTANTQR_INTEGRATION";

    protected IntegrationTestBase(ITestOutputHelper output) => Output = output;

    /// <summary>Sortie du test, seul endroit ou la raison du saut peut etre lue.</summary>
    protected ITestOutputHelper Output { get; }

    /// <summary>Adresse du service Python, surchargeable par l'environnement.</summary>
    protected static string EmbeddingServiceUrl =>
        Environment.GetEnvironmentVariable("ASSISTANTQR_EMBEDDINGS_URL") ?? "http://localhost:8088";

    /// <summary>Adresse du serveur Ollama, surchargeable par l'environnement.</summary>
    protected static string OllamaUrl =>
        Environment.GetEnvironmentVariable("ASSISTANTQR_OLLAMA_URL") ?? "http://localhost:11434";

    /// <summary>Modele Ollama interroge, surchargeable par l'environnement.</summary>
    protected static string OllamaModel =>
        Environment.GetEnvironmentVariable("ASSISTANTQR_OLLAMA_MODEL") ?? "granite4.2:3b";

    private static bool IsEnabled =>
        string.Equals(Environment.GetEnvironmentVariable(SwitchName), "1", StringComparison.Ordinal);

    /// <summary>
    /// Rend vrai si le test doit s'arreter tout de suite, apres avoir explique pourquoi.
    /// A appeler en premiere ligne de chaque test d'integration.
    /// </summary>
    protected bool ShouldSkip()
    {
        if (IsEnabled)
        {
            return false;
        }

        Output.WriteLine(
            $"Test d'intégration ignoré : la variable {SwitchName} ne vaut pas « 1 ». " +
            $"Pour l'exécuter : {SwitchName}=1 dotnet test --filter Category={CategoryName}");

        return true;
    }

    /// <summary>Client HTTP jetable, avec un delai court : un service absent doit se voir vite.</summary>
    protected static HttpClient CreateClient(string baseAddress, int timeoutSeconds = 60) =>
        new()
        {
            BaseAddress = new Uri(baseAddress, UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(timeoutSeconds),
        };
}
