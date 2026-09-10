using AssistantQR.Application.Configuration;
using AssistantQR.Application.UseCases.Snapshots;
using AssistantQR.Infrastructure.Snapshots;
using AssistantQR.Infrastructure.Tests.Support;

using Xunit;

namespace AssistantQR.Infrastructure.Tests.Snapshots;

/// <summary>
/// Tests du magasin d'instantanes au format JSON.
/// </summary>
/// <remarks>
/// DEUX PROPRIETES SONT TESTEES ICI POUR DES RAISONS TRES DIFFERENTES.
/// L'aller-retour verifie ce qu'on attend de tout magasin : ce qui entre ressort.
/// Le test sur les accents, lui, verifie une decision de LISIBILITE : un instantane se
/// relit a l'oeil et se compare avec <c>git diff</c>. Si « médiathèque » s'ecrivait
/// <c>médiathèque</c>, la comparaison d'instantanes resterait techniquement
/// correcte et pratiquement inutilisable — et la methode entiere perdrait son interet.
/// C'est le genre de propriete qu'aucun compilateur ne verifie et qu'une relecture
/// distraite laisse passer.
/// </remarks>
public sealed class JsonFileSnapshotStoreTests
{
    [Fact]
    public async Task SaveAsync_PuisLoadAsync_RestitueLIntegraliteDeLInstantane()
    {
        using var directory = new TempDirectory("instantanes-aller-retour");
        var store = new JsonFileSnapshotStore(directory.FullPath);

        var snapshot = BuildSnapshot("reference");

        await store.SaveAsync(snapshot);
        var loaded = await store.LoadAsync("reference");

        Assert.NotNull(loaded);
        Assert.Equal(snapshot.Name, loaded!.Name);
        Assert.Equal(snapshot.CreatedAt, loaded.CreatedAt);
        Assert.Equal(snapshot.Configuration, loaded.Configuration);
        Assert.Equal(snapshot.Entries.Count, loaded.Entries.Count);

        var expected = snapshot.Entries[0];
        var actual = loaded.Entries[0];

        Assert.Equal(expected.QuestionText, actual.QuestionText);
        Assert.Equal(expected.UserId, actual.UserId);
        Assert.Equal(expected.Clearance, actual.Clearance);
        Assert.Equal(expected.Answered, actual.Answered);
        Assert.Equal(expected.AnswerText, actual.AnswerText);
        Assert.Equal(expected.RefusalReason, actual.RefusalReason);
        Assert.Equal(expected.CitedDocumentIds, actual.CitedDocumentIds);
        Assert.Equal(expected.Scores, actual.Scores);
    }

    [Fact]
    public async Task SaveAsync_ModeDeFiltrage_EstEcritSousSonNomEtNonSousSonRang()
    {
        using var directory = new TempDirectory("instantanes-enumeration");
        var store = new JsonFileSnapshotStore(directory.FullPath);

        await store.SaveAsync(BuildSnapshot("enumeration"));

        var raw = await File.ReadAllTextAsync(Path.Combine(directory.FullPath, "enumeration.json"));

        // Un motif de refus enregistre comme « 3 » deviendrait faux le jour ou l'on
        // insere une valeur dans l'enumeration. Le nom, lui, survit au reordonnancement.
        Assert.Contains("\"Post\"", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveAsync_TexteAccentue_ResteLisibleDansLeFichier()
    {
        using var directory = new TempDirectory("instantanes-accents");
        var store = new JsonFileSnapshotStore(directory.FullPath);

        await store.SaveAsync(BuildSnapshot("accents"));

        var raw = await File.ReadAllTextAsync(Path.Combine(directory.FullPath, "accents.json"));

        Assert.Contains("médiathèque", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u00e9", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveAsync_Fichier_EstIndenteEtDoncRelisibleParUnHumain()
    {
        using var directory = new TempDirectory("instantanes-indentation");
        var store = new JsonFileSnapshotStore(directory.FullPath);

        await store.SaveAsync(BuildSnapshot("indentation"));

        var raw = await File.ReadAllTextAsync(Path.Combine(directory.FullPath, "indentation.json"));

        // Une ligne unique de trente mille caracteres rendrait la revue impossible.
        Assert.Contains("\n", raw, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("profil local/2026", "profil_local_2026")]
    [InlineData("../../etc/passwd", ".._.._etc_passwd")]
    [InlineData("  base de référence  ", "base_de_r_f_rence")]
    public async Task SaveAsync_NomDInstantane_EstAssainiEnNomDeFichier(string name, string expectedFileName)
    {
        using var directory = new TempDirectory("instantanes-assainissement");
        var store = new JsonFileSnapshotStore(directory.FullPath);

        await store.SaveAsync(BuildSnapshot(name));

        // Le nom vient d'une ligne de commande : sans assainissement, « ../../etc/passwd »
        // designerait un chemin hors du dossier prevu.
        Assert.True(
            File.Exists(Path.Combine(directory.FullPath, expectedFileName + ".json")),
            $"Le fichier « {expectedFileName}.json » devrait exister.");

        // Le meme nom logique doit relire le meme fichier : l'assainissement est
        // applique des deux cotes, sinon un instantane serait ecrit puis introuvable.
        Assert.NotNull(await store.LoadAsync(name));
    }

    [Fact]
    public async Task LoadAsync_InstantaneAbsent_RenvoieNull()
    {
        using var directory = new TempDirectory("instantanes-absent");
        var store = new JsonFileSnapshotStore(directory.FullPath);

        // Absence n'est pas anomalie : c'est l'appelant qui decide si un instantane de
        // reference manquant est une erreur ou une premiere execution.
        Assert.Null(await store.LoadAsync("jamais-enregistre"));
    }

    [Fact]
    public async Task ListAsync_DossierInexistant_RenvoieUneListeVide()
    {
        using var directory = new TempDirectory("instantanes-dossier-absent");
        var store = new JsonFileSnapshotStore(directory.Combine("pas-encore-cree"));

        Assert.Empty(await store.ListAsync());
    }

    [Fact]
    public async Task ListAsync_PlusieursInstantanes_LesRendTriesParNom()
    {
        using var directory = new TempDirectory("instantanes-liste");
        var store = new JsonFileSnapshotStore(directory.FullPath);

        await store.SaveAsync(BuildSnapshot("candidat"));
        await store.SaveAsync(BuildSnapshot("reference"));

        var names = await store.ListAsync();

        Assert.Equal(new[] { "candidat", "reference" }, names);
    }

    [Fact]
    public async Task SaveAsync_DeuxFoisLeMemeNom_EcraseLInstantanePrecedent()
    {
        using var directory = new TempDirectory("instantanes-ecrasement");
        var store = new JsonFileSnapshotStore(directory.FullPath);

        await store.SaveAsync(BuildSnapshot("reference"));
        await store.SaveAsync(BuildSnapshot("reference") with { Entries = Array.Empty<SnapshotEntry>() });

        var loaded = await store.LoadAsync("reference");

        Assert.NotNull(loaded);
        Assert.Empty(loaded!.Entries);
        Assert.Single(await store.ListAsync());
    }

    [Fact]
    public async Task LoadAsync_FichierCorrompu_LeveEnNommantLeFichier()
    {
        using var directory = new TempDirectory("instantanes-corrompus");
        directory.Write("corrompu.json", "{ ceci n'est pas du JSON");

        var store = new JsonFileSnapshotStore(directory.FullPath);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync("corrompu"));

        Assert.Contains("corrompu.json", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructeur_DossierNonRenseigne_EstRefuse()
    {
        Assert.Throws<ArgumentException>(() => new JsonFileSnapshotStore("   "));
    }

    private static EvaluationSnapshot BuildSnapshot(string name)
    {
        var configuration = new ConfigurationFingerprint(
            EmbeddingModel: "hashing-fake",
            Dimension: 256,
            ChunkingStrategyId: "paragraph",
            FilterMode: AccessFilterMode.Post,
            TopK: 4,
            MinScore: 0.20,
            PromptName: "answer-with-citations",
            PromptVersion: "1.0.0",
            PromptFingerprint: "0123456789ab",
            LlmModelId: "extractive-fake",
            Temperature: 0.0,
            Seed: 42);

        var entries = new[]
        {
            new SnapshotEntry(
                QuestionText: "Quels sont les horaires d'ouverture de la médiathèque le samedi ?",
                UserId: "visiteur",
                Clearance: "public",
                Answered: true,
                AnswerText: "La médiathèque ouvre de dix heures à dix-neuf heures. [horaires-ouverture]",
                RefusalReason: null,
                CitedDocumentIds: new[] { "horaires-ouverture" },
                Scores: new[] { 0.87, 0.42 }),
            new SnapshotEntry(
                QuestionText: "Quelle est la grille de rémunération des agents ?",
                UserId: "agent",
                Clearance: "internal",
                Answered: false,
                AnswerText: null,
                RefusalReason: "NoEvidenceReadableByRequester",
                CitedDocumentIds: Array.Empty<string>(),
                Scores: Array.Empty<double>()),
        };

        return new EvaluationSnapshot(
            name,
            new DateTimeOffset(2026, 3, 14, 9, 30, 0, TimeSpan.Zero),
            configuration,
            entries);
    }
}
