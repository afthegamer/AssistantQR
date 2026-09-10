using AssistantQR.Application.Configuration;
using AssistantQR.Application.Model;
using AssistantQR.Application.Tests.Doubles;
using AssistantQR.Application.UseCases.AnswerQuestion;
using AssistantQR.Application.UseCases.Snapshots;

using AssistantQR.Domain.Access;

using Xunit;

namespace AssistantQR.Application.Tests.UseCases;

/// <summary>
/// COMMENT TESTER UN SYSTEME DONT UN COMPOSANT EST NON DETERMINISTE.
///
/// On ne peut pas ecrire « la reponse doit etre ceci » : aucun modele de langue ne le
/// garantit. On peut en revanche ecrire « la reponse ne doit pas avoir change depuis
/// qu'on l'a relue et acceptee ». C'est le principe des tests d'approbation, transpose
/// a une application d'IA — on ne valide pas une sortie, on surveille une derive.
///
/// La comparaison ne dit jamais si le systeme s'est ameliore : aucune machine ne peut
/// le dire ici. Elle dit CE QUI a change et A QUEL POINT, en mettant la derive en
/// regard des differences de configuration. Une derive de 40 % assortie d'un changement
/// de version de prompt est un resultat attendu ; la meme derive sans aucune difference
/// de configuration est une alerte.
/// </summary>
public sealed class SnapshotTests
{
    // -----------------------------------------------------------------------
    // Enregistrement
    // -----------------------------------------------------------------------

    private static AnswerQuestionUseCase AnswerUseCase(ScriptedLanguageModel model)
    {
        var index = new FakeVectorIndex();
        index.Metadata = new IndexMetadata("axis-fake", 2, "paragraph", 2, FakeVectorIndex.BuiltAtStub);
        index.SeedScored("horaires-ouverture", "Horaires d'ouverture au public", AccessLevel.Public, 0.90,
            "Ouverture continue le samedi de 10 h a 18 h.");
        index.SeedScored("planning-agents", "Elaboration du planning des agents", AccessLevel.Internal, 0.80,
            "Le planning est arrete un mois a l'avance.");

        return new AnswerQuestionUseCase(
            new AxisEmbeddingService(),
            index,
            model,
            FakePromptCatalog.Default(),
            PipelineOptions.Default with { TopK = 3, MinScore = 0.20 });
    }

    private static readonly IReadOnlyList<QuestionSetItem> QuestionSet = new[]
    {
        new QuestionSetItem("Quels sont les horaires du samedi ?", "visiteur", "public"),
        new QuestionSetItem("Quels sont les horaires du samedi ?", "agent-42", "internal"),
        new QuestionSetItem("Comment est etabli le planning ?", "agent-42", "internal"),
    };

    [Fact]
    public async Task RecordSnapshot_ProducesOneEntryPerQuestion()
    {
        var store = new FakeSnapshotStore();
        var clock = new FixedClock();
        var model = new ScriptedLanguageModel("Ouverture continue le samedi [horaires-ouverture].");

        var snapshot = await new RecordSnapshotUseCase(AnswerUseCase(model), store, clock)
            .ExecuteAsync(new RecordSnapshotCommand("reference", QuestionSet));

        Assert.Equal(3, snapshot.Entries.Count);
        Assert.Equal("reference", snapshot.Name);

        // L'horloge figee est ce qui rend un instantane reproductible au caractere pres.
        // Avec DateTimeOffset.UtcNow ecrit en dur, deux enregistrements du meme jeu de
        // questions differeraient toujours et la comparaison serait inutilisable.
        Assert.Equal(FixedClock.DefaultInstant, snapshot.CreatedAt);
        Assert.Single(store.Saved);
    }

    [Fact]
    public async Task RecordSnapshot_KeepsTheRequesterIdentityWithEachEntry()
    {
        var store = new FakeSnapshotStore();
        var model = new ScriptedLanguageModel("Ouverture continue le samedi [horaires-ouverture].");

        var snapshot = await new RecordSnapshotUseCase(AnswerUseCase(model), store, new FixedClock())
            .ExecuteAsync(new RecordSnapshotCommand("reference", QuestionSet));

        // La meme question posee par un visiteur et par un agent sont DEUX cas de test.
        // Un jeu de questions qui oublierait le demandeur ne pourrait pas tester le
        // controle d'acces.
        Assert.Equal("visiteur", snapshot.Entries[0].UserId);
        Assert.Equal("agent-42", snapshot.Entries[1].UserId);
        Assert.Equal(snapshot.Entries[0].QuestionText, snapshot.Entries[1].QuestionText);
    }

    [Fact]
    public async Task RecordSnapshot_RecordsAnswersCitationsAndScores()
    {
        var store = new FakeSnapshotStore();
        var model = new ScriptedLanguageModel("Ouverture continue le samedi [horaires-ouverture].");

        var snapshot = await new RecordSnapshotUseCase(AnswerUseCase(model), store, new FixedClock())
            .ExecuteAsync(new RecordSnapshotCommand("reference", QuestionSet));

        var first = snapshot.Entries[0];

        Assert.True(first.Answered);
        Assert.Null(first.RefusalReason);
        Assert.Equal(new[] { "horaires-ouverture" }, first.CitedDocumentIds);

        // Les scores sont conserves : une reponse identique obtenue avec des scores tres
        // differents signale une derive de la RECHERCHE que le seul texte ne revelerait pas.
        Assert.NotEmpty(first.Scores);
    }

    [Fact]
    public async Task RecordSnapshot_RecordsRefusalsAsFirstClassResults()
    {
        var store = new FakeSnapshotStore();
        var model = new ScriptedLanguageModel("Texte sans la moindre source.");

        var snapshot = await new RecordSnapshotUseCase(AnswerUseCase(model), store, new FixedClock())
            .ExecuteAsync(new RecordSnapshotCommand("reference", QuestionSet));

        // Un refus n'est pas un echec du test : c'est un comportement a surveiller comme
        // un autre. Un systeme qui se met a repondre la ou il refusait a change de
        // comportement, et c'est la derive la plus grave qu'on puisse detecter.
        Assert.All(snapshot.Entries, entry => Assert.False(entry.Answered));
        Assert.All(snapshot.Entries, entry => Assert.Equal("ModelProducedNoCitation", entry.RefusalReason));
    }

    [Fact]
    public async Task RecordSnapshot_TakesTheConfigurationFingerprintFromTheFirstQuestion()
    {
        var store = new FakeSnapshotStore();
        var model = new ScriptedLanguageModel("Ouverture continue le samedi [horaires-ouverture].", "granite4.2:3b");

        var snapshot = await new RecordSnapshotUseCase(AnswerUseCase(model), store, new FixedClock())
            .ExecuteAsync(new RecordSnapshotCommand("reference", QuestionSet));

        Assert.Equal("axis-fake", snapshot.Configuration.EmbeddingModel);
        Assert.Equal("paragraph", snapshot.Configuration.ChunkingStrategyId);
        Assert.Equal("granite4.2:3b", snapshot.Configuration.LlmModelId);
    }

    [Fact]
    public async Task RecordSnapshot_EmptyQuestionSet_Throws()
    {
        var useCase = new RecordSnapshotUseCase(
            AnswerUseCase(new ScriptedLanguageModel("x")), new FakeSnapshotStore(), new FixedClock());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => useCase.ExecuteAsync(new RecordSnapshotCommand("vide", Array.Empty<QuestionSetItem>())));
    }

    // -----------------------------------------------------------------------
    // Comparaison
    // -----------------------------------------------------------------------

    private static SnapshotEntry Entry(
        string question,
        bool answered,
        string? answerText,
        string? refusalReason,
        params string[] citedIds) =>
        new(question, "agent-42", "internal", answered, answerText, refusalReason, citedIds, new[] { 0.9 });

    private static EvaluationSnapshot Snapshot(string name, params SnapshotEntry[] entries) =>
        new(name, FixedClock.DefaultInstant, ConfigurationFingerprint.Empty, entries);

    private static async Task<SnapshotComparison> CompareAsync(
        EvaluationSnapshot baseline,
        EvaluationSnapshot candidate)
    {
        var store = new FakeSnapshotStore().With(baseline).With(candidate);
        return await new CompareSnapshotsUseCase(store).ExecuteAsync(baseline.Name, candidate.Name);
    }

    [Fact]
    public async Task CompareSnapshots_SameBehaviour_ReportsIdentical()
    {
        var entry = Entry("Horaires ?", true, "Ouvert le samedi [horaires-ouverture].", null, "horaires-ouverture");

        var comparison = await CompareAsync(
            Snapshot("avant", entry),
            Snapshot("apres", entry with { }));

        Assert.Equal(DifferenceKind.Identical, comparison.Differences[0].Kind);
        Assert.Equal(0, comparison.ChangedCount);
        Assert.Equal(0d, comparison.DriftRatio);
    }

    [Fact]
    public async Task CompareSnapshots_OnlyTheWordingChanged_ReportsAnswerTextChanged()
    {
        var comparison = await CompareAsync(
            Snapshot("avant", Entry("Horaires ?", true, "Ouvert le samedi [horaires-ouverture].", null, "horaires-ouverture")),
            Snapshot("apres", Entry("Horaires ?", true, "La mediatheque ouvre le samedi [horaires-ouverture].", null, "horaires-ouverture")));

        // La derive la plus benigne : memes sources, meme decision, reformulation. C'est
        // le bruit attendu d'un modele de langue, et le noyer dans les vraies alertes
        // serait le meilleur moyen de ne plus lire aucun rapport.
        Assert.Equal(DifferenceKind.AnswerTextChanged, comparison.Differences[0].Kind);
    }

    [Fact]
    public async Task CompareSnapshots_WhitespaceOnlyDifference_ReportsIdentical()
    {
        var comparison = await CompareAsync(
            Snapshot("avant", Entry("Horaires ?", true, "Ouvert le samedi.", null, "horaires-ouverture")),
            Snapshot("apres", Entry("Horaires ?", true, "Ouvert   le\n samedi.", null, "horaires-ouverture")));

        // Un retour a la ligne de plus n'est pas un changement de comportement.
        Assert.Equal(DifferenceKind.Identical, comparison.Differences[0].Kind);
    }

    [Fact]
    public async Task CompareSnapshots_SourcesChanged_ReportsCitationsChanged()
    {
        var comparison = await CompareAsync(
            Snapshot("avant", Entry("Horaires ?", true, "Texte.", null, "horaires-ouverture")),
            Snapshot("apres", Entry("Horaires ?", true, "Texte.", null, "horaires-ouverture", "accessibilite")));

        // La reponse est la meme mais elle ne s'appuie plus sur le meme materiau : c'est
        // le symptome typique d'un changement de decoupage ou de modele d'embeddings.
        Assert.Equal(DifferenceKind.CitationsChanged, comparison.Differences[0].Kind);
        Assert.Contains("accessibilite", comparison.Differences[0].Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompareSnapshots_CitationOrderChanged_ReportsCitationsChanged()
    {
        var comparison = await CompareAsync(
            Snapshot("avant", Entry("Horaires ?", true, "Texte.", null, "horaires-ouverture", "accessibilite")),
            Snapshot("apres", Entry("Horaires ?", true, "Texte.", null, "accessibilite", "horaires-ouverture")));

        Assert.Equal(DifferenceKind.CitationsChanged, comparison.Differences[0].Kind);
    }

    [Fact]
    public async Task CompareSnapshots_AnswerBecameARefusal_ReportsRefusalChanged()
    {
        var comparison = await CompareAsync(
            Snapshot("avant", Entry("Horaires ?", true, "Ouvert le samedi.", null, "horaires-ouverture")),
            Snapshot("apres", Entry("Horaires ?", false, null, "NoEvidenceReadableByRequester")));

        // Le plus grave, et donc le premier evalue. Une question qui passe du refus a la
        // reponse a forcement des citations et un texte differents : la signaler comme
        // « texte modifie » enterrerait l'information importante sous la plus anodine.
        Assert.Equal(DifferenceKind.RefusalChanged, comparison.Differences[0].Kind);
    }

    [Fact]
    public async Task CompareSnapshots_RefusalReasonChanged_ReportsRefusalChanged()
    {
        var comparison = await CompareAsync(
            Snapshot("avant", Entry("Piscine ?", false, null, "NoEvidenceInCorpus")),
            Snapshot("apres", Entry("Piscine ?", false, null, "NoEvidenceReadableByRequester")));

        // Deux refus ne se valent pas : « rien dans le corpus » et « rien pour vous »
        // decrivent deux systemes differents.
        Assert.Equal(DifferenceKind.RefusalChanged, comparison.Differences[0].Kind);
    }

    [Fact]
    public async Task CompareSnapshots_QuestionRemoved_ReportsMissingInCandidate()
    {
        var comparison = await CompareAsync(
            Snapshot("avant",
                Entry("Horaires ?", true, "Texte.", null, "horaires-ouverture"),
                Entry("Amendes ?", true, "Texte.", null, "retards-amendes")),
            Snapshot("apres", Entry("Horaires ?", true, "Texte.", null, "horaires-ouverture")));

        Assert.Contains(comparison.Differences, d => d.Kind == DifferenceKind.MissingInCandidate);
    }

    [Fact]
    public async Task CompareSnapshots_QuestionAdded_ReportsMissingInBaseline()
    {
        var comparison = await CompareAsync(
            Snapshot("avant", Entry("Horaires ?", true, "Texte.", null, "horaires-ouverture")),
            Snapshot("apres",
                Entry("Horaires ?", true, "Texte.", null, "horaires-ouverture"),
                Entry("Amendes ?", true, "Texte.", null, "retards-amendes")));

        Assert.Contains(comparison.Differences, d => d.Kind == DifferenceKind.MissingInBaseline);
    }

    [Fact]
    public async Task CompareSnapshots_DriftRatio_IsTheProportionOfChangedQuestions()
    {
        var stable = Entry("Horaires ?", true, "Texte.", null, "horaires-ouverture");
        var alsoStable = Entry("Accessibilite ?", true, "Texte.", null, "accessibilite");

        var comparison = await CompareAsync(
            Snapshot("avant", stable, alsoStable, Entry("Amendes ?", true, "Texte.", null, "retards-amendes"), Entry("Piscine ?", false, null, "NoEvidenceInCorpus")),
            Snapshot("apres", stable, alsoStable, Entry("Amendes ?", true, "Texte.", null, "gestion-retards-interne"), Entry("Piscine ?", true, "Texte.", null, "dons-documents")));

        Assert.Equal(4, comparison.TotalCount);
        Assert.Equal(2, comparison.ChangedCount);
        Assert.Equal(0.5d, comparison.DriftRatio);
    }

    [Fact]
    public async Task CompareSnapshots_EmptySnapshots_DriftRatioIsZeroNotOne()
    {
        // Aucune comparaison ne signifie aucune derive constatee, pas une derive totale.
        var comparison = await CompareAsync(Snapshot("avant"), Snapshot("apres"));

        Assert.Equal(0, comparison.TotalCount);
        Assert.Equal(0d, comparison.DriftRatio);
    }

    [Fact]
    public async Task CompareSnapshots_UnknownBaseline_Throws()
    {
        var store = new FakeSnapshotStore().With(Snapshot("apres"));
        var useCase = new CompareSnapshotsUseCase(store);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => useCase.ExecuteAsync("inexistant", "apres"));

        Assert.Contains("inexistant", error.Message, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // Empreinte de configuration
    // -----------------------------------------------------------------------

    private static ConfigurationFingerprint Fingerprint() => new(
        EmbeddingModel: "hashing-fake",
        Dimension: 256,
        ChunkingStrategyId: "paragraph",
        FilterMode: AccessFilterMode.Post,
        TopK: 4,
        MinScore: 0.20,
        PromptName: "answer-with-citations",
        PromptVersion: "1.0.0",
        PromptFingerprint: "abc123def456",
        LlmModelId: "extractive-fake",
        Temperature: 0,
        Seed: 42);

    [Fact]
    public void DifferencesWith_IdenticalConfigurations_ReturnsNothing()
    {
        Assert.Empty(Fingerprint().DifferencesWith(Fingerprint()));
    }

    [Fact]
    public void DifferencesWith_ChunkingStrategyChanged_NamesTheFieldAndBothValues()
    {
        var differences = Fingerprint().DifferencesWith(Fingerprint() with { ChunkingStrategyId = "fixed-600-100" });

        // C'est LA phrase qui repond a « pourquoi les reponses ont-elles change ? ».
        // Sans empreinte, on constate la derive et on ne peut rien lui imputer.
        var single = Assert.Single(differences);
        Assert.Contains("ChunkingStrategyId", single, StringComparison.Ordinal);
        Assert.Contains("paragraph", single, StringComparison.Ordinal);
        Assert.Contains("fixed-600-100", single, StringComparison.Ordinal);
    }

    [Fact]
    public void DifferencesWith_SeveralFieldsChanged_ListsThemAll()
    {
        var differences = Fingerprint().DifferencesWith(Fingerprint() with
        {
            PromptVersion = "1.1.0",
            PromptFingerprint = "999888777666",
            Temperature = 0.7,
            FilterMode = AccessFilterMode.Pre,
        });

        Assert.Equal(4, differences.Count);
        Assert.Contains(differences, d => d.StartsWith("PromptVersion", StringComparison.Ordinal));
        Assert.Contains(differences, d => d.StartsWith("Temperature", StringComparison.Ordinal));
        Assert.Contains(differences, d => d.StartsWith("FilterMode", StringComparison.Ordinal));
    }

    [Fact]
    public void DifferencesWith_SeedRemoved_IsReportedInPlainWords()
    {
        var differences = Fingerprint().DifferencesWith(Fingerprint() with { Seed = null });

        Assert.Contains("(aucune)", Assert.Single(differences), StringComparison.Ordinal);
    }

    [Fact]
    public void DifferencesWith_PromptEditedWithoutVersionBump_IsStillDetected()
    {
        // Le cas le plus insidieux : on corrige un prompt sans penser a en changer la
        // version. Le numero de version ment, l'empreinte non.
        var differences = Fingerprint().DifferencesWith(Fingerprint() with { PromptFingerprint = "000111222333" });

        Assert.Contains("PromptFingerprint", Assert.Single(differences), StringComparison.Ordinal);
    }

    [Fact]
    public void ToString_IsASingleCompactLine()
    {
        var line = Fingerprint().ToString();

        Assert.DoesNotContain("\n", line, StringComparison.Ordinal);
        Assert.Contains("hashing-fake", line, StringComparison.Ordinal);
        Assert.Contains("answer-with-citations@1.0.0#abc123def456", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_IsANeutralFingerprint()
    {
        Assert.Equal(string.Empty, ConfigurationFingerprint.Empty.EmbeddingModel);
        Assert.Null(ConfigurationFingerprint.Empty.Seed);
    }
}
