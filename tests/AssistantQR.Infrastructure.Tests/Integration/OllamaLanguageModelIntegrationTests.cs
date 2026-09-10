using AssistantQR.Application.Model;
using AssistantQR.Infrastructure.Http;
using AssistantQR.Infrastructure.LanguageModels;

using Xunit;
using Xunit.Abstractions;

namespace AssistantQR.Infrastructure.Tests.Integration;

/// <summary>
/// Tests de l'adaptateur Ollama contre un serveur reel.
/// </summary>
/// <remarks>
/// CE QU'ILS VERIFIENT ET CE QU'ILS NE VERIFIENT PAS. Ils verifient le CONTRAT HTTP :
/// le chemin, les noms de champs, la lecture de la reponse, la traduction des statuts
/// d'erreur en messages exploitables. Ils ne verifient rien sur la QUALITE de la
/// generation, et ne le peuvent pas : un modele de langue ne rend pas deux fois la meme
/// chose, meme a temperature nulle — ordre de reduction en virgule flottante sur GPU,
/// changement de version du serveur, mise a jour du modele. Toute assertion sur le
/// contenu produit serait un test qui echoue un jour sans que rien n'ait ete casse.
///
/// C'est exactement pour cela que la garantie metier n'est pas confiee au modele mais a
/// <c>AnswerPolicy</c>, testee hors ligne, sans reseau et sans surprise.
/// </remarks>
[Trait("Category", IntegrationTestBase.CategoryName)]
public sealed class OllamaLanguageModelIntegrationTests : IntegrationTestBase
{
    public OllamaLanguageModelIntegrationTests(ITestOutputHelper output)
        : base(output)
    {
    }

    [Fact]
    public async Task CompleteAsync_ServeurReel_RendUnTexteNonVideEtSonModele()
    {
        if (ShouldSkip())
        {
            return;
        }

        using var client = CreateClient(OllamaUrl, timeoutSeconds: 180);
        var model = new OllamaLanguageModel(client, OllamaModel);

        var completion = await model.CompleteAsync(new LlmRequest(
            "Réponds par un seul mot : quelle est la capitale de la France ?",
            Temperature: 0.0,
            Seed: 42,
            MaxTokens: 32));

        Output.WriteLine($"Réponse d'Ollama : {completion.Text}");

        Assert.False(string.IsNullOrWhiteSpace(completion.Text));
        Assert.Equal(OllamaModel, completion.ModelId);
    }

    [Fact]
    public async Task CompleteAsync_ModeleInexistant_LeveAvecLaCommandeAExecuter()
    {
        if (ShouldSkip())
        {
            return;
        }

        using var client = CreateClient(OllamaUrl);
        var model = new OllamaLanguageModel(client, "modele-qui-n-existe-pas:0b");

        var exception = await Assert.ThrowsAsync<LanguageModelException>(
            () => model.CompleteAsync(new LlmRequest("Bonjour.", 0.0, 42, 16)));

        Output.WriteLine(exception.Message);

        // Un adaptateur qui se contente de relayer « 404 Not Found » a fait la moitie du
        // travail : le message doit dire quoi taper pour s'en sortir.
        Assert.Contains("ollama pull", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CompleteAsync_ServeurInjoignable_LeveAvecUneExplicationFrancaise()
    {
        if (ShouldSkip())
        {
            return;
        }

        // Port volontairement mort : on teste la traduction d'une panne de transport, pas
        // le comportement du serveur.
        using var client = CreateClient("http://127.0.0.1:1", timeoutSeconds: 5);
        var model = new OllamaLanguageModel(client, OllamaModel);

        var exception = await Assert.ThrowsAsync<LanguageModelException>(
            () => model.CompleteAsync(new LlmRequest("Bonjour.", 0.0, 42, 16)));

        Output.WriteLine(exception.Message);

        Assert.Contains("Ollama", exception.Message, StringComparison.Ordinal);
    }
}
