using AssistantQR.Application.Model;
using AssistantQR.Application.Ports;
using AssistantQR.Infrastructure.Http;
using AssistantQR.Infrastructure.LanguageModels;
using AssistantQR.Infrastructure.Tests.Support;

using Xunit;

namespace AssistantQR.Infrastructure.Tests.LanguageModels;

/// <summary>
/// Tests du faux de rejeu.
/// </summary>
/// <remarks>
/// LE REJEU EST L'OUTIL QUI PERMET DE FABRIQUER DES CAS PATHOLOGIQUES A LA DEMANDE :
/// une reponse qui cite un document inexistant, une qui cite un document confidentiel,
/// une qui ne cite rien. Chacun declenche un motif de refus different, et c'est ainsi
/// qu'on montre qu'<c>AnswerPolicy</c> tient sans dependre du modele. Les tests
/// ci-dessous ne verifient donc pas la « qualite » des reponses — le rejeu n'en a
/// aucune — mais les trois proprietes dont depend cette utilite : l'appariement est
/// previsible, l'ordre du fichier fait loi, et le repli existe.
/// </remarks>
public sealed class ReplayLanguageModelTests
{
    [Theory]
    [InlineData("Quels sont les HORAIRES d'ouverture ?")]
    [InlineData("quels sont les horaires d'ouverture ?")]
    [InlineData("Question sur les Horaires")]
    public async Task CompleteAsync_AppariementInsensibleALaCasse(string prompt)
    {
        var model = new ReplayLanguageModel(new[]
        {
            new ReplayEntry("horaires", "La médiathèque ouvre à dix heures. [horaires-ouverture]"),
        });

        var completion = await model.CompleteAsync(Request(prompt));

        Assert.Contains("[horaires-ouverture]", completion.Text, StringComparison.Ordinal);
        Assert.Equal("replay", completion.ModelId);
    }

    [Theory]
    [InlineData("médiathèque", "Une question sur la mediatheque municipale.")]
    [InlineData("mediatheque", "Une question sur la médiathèque municipale.")]
    [InlineData("Désherbage", "Le protocole de desherbage des collections.")]
    public async Task CompleteAsync_AppariementInsensibleAuxAccents(string match, string prompt)
    {
        var model = new ReplayLanguageModel(new[] { new ReplayEntry(match, "Réponse enregistrée. [accueil]") });

        var completion = await model.CompleteAsync(Request(prompt));

        // Le repliement du texte est le meme que celui de l'embedding factice, et pour
        // la meme raison : un jeu de rejeu ecrit sans accents doit retrouver une
        // question qui en porte.
        Assert.Equal("Réponse enregistrée. [accueil]", completion.Text);
    }

    [Fact]
    public async Task CompleteAsync_PlusieursMotifsApplicables_LePremierGagne()
    {
        var model = new ReplayLanguageModel(new[]
        {
            new ReplayEntry("retards de l'usager", "Réponse spécifique. [gestion-retards-interne]"),
            new ReplayEntry("retards", "Réponse générale. [retards-amendes]"),
        });

        var completion = await model.CompleteAsync(Request("Comment traiter les retards de l'usager en contentieux ?"));

        // L'ordre du fichier est significatif, du plus specifique au plus general.
        Assert.Equal("Réponse spécifique. [gestion-retards-interne]", completion.Text);
    }

    [Fact]
    public async Task CompleteAsync_AucunMotif_UtiliseLeRepliEtAnnonceSonModele()
    {
        var fallback = new StubLanguageModel("modele-de-repli", "Réponse du repli. [horaires-ouverture]");
        var model = new ReplayLanguageModel(new[] { new ReplayEntry("horaires", "jamais servi") }, fallback);

        var completion = await model.CompleteAsync(Request("Une question qui ne ressemble à rien de connu."));

        Assert.Equal("Réponse du repli. [horaires-ouverture]", completion.Text);

        // La trace doit dire quel modele a REELLEMENT parle, pas lequel etait configure.
        Assert.Equal("modele-de-repli", completion.ModelId);
    }

    [Fact]
    public async Task CompleteAsync_AucunMotifEtAucunRepli_RenvoieLeMarqueurDeRefus()
    {
        var model = new ReplayLanguageModel(new[] { new ReplayEntry("horaires", "jamais servi") });

        var completion = await model.CompleteAsync(Request("Une question hors de tout jeu de rejeu."));

        // On refuse explicitement plutot que de rendre une chaine vide, qui serait
        // interpretee comme « le modele a repondu du vide » — un motif de refus different.
        Assert.Equal(ModelResponseParser.RefusalMarker, completion.Text);
        Assert.Equal("replay", completion.ModelId);
    }

    [Fact]
    public async Task CompleteAsync_MotifVide_NAppariePasTout()
    {
        var fallback = new StubLanguageModel("repli", "Réponse du repli.");
        var model = new ReplayLanguageModel(
            new[] { new ReplayEntry("   ", "fourre-tout involontaire"), new ReplayEntry("horaires", "servi") },
            fallback);

        var completion = await model.CompleteAsync(Request("Une question sans rapport."));

        Assert.Equal("Réponse du repli.", completion.Text);
    }

    [Fact]
    public async Task CompleteAsync_JeuDeRejeuVide_TombeSurLeRepli()
    {
        var fallback = new StubLanguageModel("repli", "Réponse du repli.");
        var model = new ReplayLanguageModel(Array.Empty<ReplayEntry>(), fallback);

        var completion = await model.CompleteAsync(Request("N'importe quelle question."));

        Assert.Equal("Réponse du repli.", completion.Text);
    }

    [Fact]
    public async Task FromJsonFile_FichierValide_ChargeLesEntrees()
    {
        using var directory = new TempDirectory("rejeu-valide");

        var path = directory.Write("replay.json", """
            [
              { "match": "horaires", "response": "La médiathèque ouvre à dix heures. [horaires-ouverture]" },
              { "match": "prêt", "response": "Le prêt court sur trois semaines. [pret-documents]" }
            ]
            """);

        var model = ReplayLanguageModel.FromJsonFile(path);

        var completion = await model.CompleteAsync(Request("Quels sont les horaires ?"));

        Assert.Contains("[horaires-ouverture]", completion.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void FromJsonFile_FichierAbsent_LeveAvecUneConsigneExploitable()
    {
        using var directory = new TempDirectory("rejeu-absent");

        var exception = Assert.Throws<LanguageModelException>(
            () => ReplayLanguageModel.FromJsonFile(directory.Combine("introuvable.json")));

        Assert.Contains("introuvable.json", exception.Message, StringComparison.Ordinal);
        Assert.Contains("extractive-fake", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromJsonFile_JsonInvalide_LeveEnRappelantLeFormatAttendu()
    {
        using var directory = new TempDirectory("rejeu-invalide");
        var path = directory.Write("replay.json", "ceci n'est pas du JSON");

        var exception = Assert.Throws<LanguageModelException>(() => ReplayLanguageModel.FromJsonFile(path));

        Assert.Contains("match", exception.Message, StringComparison.Ordinal);
        Assert.Contains("response", exception.Message, StringComparison.Ordinal);
    }

    private static LlmRequest Request(string prompt) => new(prompt, Temperature: 0.0, Seed: 42, MaxTokens: 600);

    /// <summary>
    /// Doublure minimale servant de repli : elle rend une reponse fixee et son propre
    /// identifiant de modele, ce qui suffit a verifier que le rejeu ne se l'attribue pas.
    /// </summary>
    private sealed class StubLanguageModel : ILanguageModel
    {
        private readonly string _text;

        public StubLanguageModel(string modelId, string text)
        {
            ModelId = modelId;
            _text = text;
        }

        public string ModelId { get; }

        public Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LlmCompletion(_text, ModelId));
    }
}
