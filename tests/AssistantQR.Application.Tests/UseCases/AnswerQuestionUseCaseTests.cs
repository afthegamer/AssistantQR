using AssistantQR.Application.Configuration;
using AssistantQR.Application.Model;
using AssistantQR.Application.Ports;
using AssistantQR.Application.Tests.Doubles;
using AssistantQR.Application.UseCases.AnswerQuestion;

using AssistantQR.Domain.Access;
using AssistantQR.Domain.Answers;
using AssistantQR.Domain.Documents;
using AssistantQR.Domain.Evidence;
using AssistantQR.Domain.Policies;
using AssistantQR.Domain.Questions;

using Xunit;

namespace AssistantQR.Application.Tests.UseCases;

/// <summary>
/// LE PIPELINE COMPLET, SANS CLE D'API, SANS RESEAU, SANS BASE VECTORIELLE.
///
/// Ce fichier est la demonstration la plus directe de ce que la Clean Architecture
/// apporte a une application d'IA. Le cas d'usage orchestre quatre ports ; les quatre
/// sont ici des doublures de quelques dizaines de lignes. Le systeme entier — recherche,
/// controle d'acces, construction du prompt, analyse de la sortie, decision — s'execute
/// en quelques millisecondes et rend TOUJOURS le meme resultat.
///
/// Le seul composant non deterministe du systeme reel, le modele de langue, est confine
/// derriere une interface a une methode. On le remplace par une doublure a sortie figee
/// et tout le reste redevient assertable, y compris — et surtout — les cas de refus,
/// que l'on ne pourrait jamais provoquer a la demande avec un vrai modele.
/// </summary>
public sealed class AnswerQuestionUseCaseTests
{
    private static readonly Requester Visitor = Requester.Create("visiteur", "public");
    private static readonly Requester Agent = Requester.Create("agent-42", "internal");

    private static AnswerQuestionCommand Ask(string text, Requester requester) =>
        new(Question.From(text), requester);

    private static AnswerQuestionUseCase Build(
        FakeVectorIndex index,
        ILanguageModel languageModel,
        PipelineOptions? options = null,
        IEmbeddingService? embeddings = null,
        IPromptCatalog? prompts = null) =>
        new(
            embeddings ?? new AxisEmbeddingService(),
            index,
            languageModel,
            prompts ?? FakePromptCatalog.Default(),
            options ?? (PipelineOptions.Default with { TopK = 3, MinScore = 0.20 }));

    /// <summary>Corpus miniature ou public et interne se disputent le vocabulaire du retard.</summary>
    private static FakeVectorIndex RetardsIndex()
    {
        var index = new FakeVectorIndex();
        index.SeedScored("retards-amendes", "Retards, relances et amendes", AccessLevel.Public, 0.90,
            "L'amende est de 0,10 euro par jour et par document.");
        index.SeedScored("gestion-retards-interne", "Traitement interne des retards", AccessLevel.Internal, 0.75,
            "La troisieme relance declenche l'envoi d'un courrier recommande.");
        index.SeedScored("contentieux-usagers", "Suivi des usagers en contentieux", AccessLevel.Confidential, 0.95,
            "Le seuil de saisine du tresor public est de 80 euros.");
        return index;
    }

    // -----------------------------------------------------------------------
    // Chemin nominal
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_ModelCitesAReadableDocument_ReturnsAnAnsweredOutcome()
    {
        var index = RetardsIndex();
        var model = new ScriptedLanguageModel("L'amende est de 0,10 euro par jour [retards-amendes].");

        var result = await Build(index, model).ExecuteAsync(Ask("Combien coute un retard ?", Visitor));

        var answered = Assert.IsType<AnswerOutcome.Answered>(result.Outcome);
        Assert.True(result.Outcome.IsAnswered);
        Assert.Single(answered.Answer.Citations);
        Assert.Equal("retards-amendes", answered.Answer.Citations[0].DocumentId.Value);
        Assert.Equal("Retards, relances et amendes", answered.Answer.Citations[0].DocumentTitle);
        Assert.Equal(1, model.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_NominalPath_FillsTheRetrievalTrace()
    {
        var index = RetardsIndex();
        var model = new ScriptedLanguageModel("Trois relances [gestion-retards-interne].");

        var result = await Build(index, model).ExecuteAsync(Ask("Combien de relances ?", Agent));

        // La trace n'est pas un journal : c'est un resultat de premiere classe. Elle dit
        // ce que l'index a propose, ce qui a survecu au controle d'acces, et ce que ce
        // controle a coute. Un refus sans trace est inexplicable a l'usager.
        Assert.Equal(3, result.Trace.CandidatesFromIndex);
        Assert.Equal(2, result.Trace.CandidatesAfterAccessFilter);
        Assert.Single(result.Trace.DroppedByAccessFilter);
        Assert.Equal(
            "contentieux-usagers",
            result.Trace.DroppedByAccessFilter[0].Fragment.DocumentId.Value);
        Assert.Equal(AccessFilterMode.Post, result.Trace.FilterMode);
        Assert.Null(result.Trace.IndexModelWarning);
    }

    [Fact]
    public async Task ExecuteAsync_NominalPath_ThePromptCarriesQuestionEvidenceAndRefusalMarker()
    {
        var index = RetardsIndex();
        var model = new ScriptedLanguageModel("Reponse [retards-amendes].");

        await Build(index, model).ExecuteAsync(Ask("Combien coute un retard ?", Visitor));

        var prompt = model.LastRequest!.Prompt;

        Assert.Contains("Combien coute un retard ?", prompt, StringComparison.Ordinal);
        Assert.Contains("[retards-amendes] Retards, relances et amendes (public)", prompt, StringComparison.Ordinal);
        Assert.Contains(ModelResponseParser.RefusalMarker, prompt, StringComparison.Ordinal);

        // Le prompt ne doit contenir AUCUN extrait interdit : le controle d'acces
        // s'applique avant la construction du prompt, pas apres la reponse. Sinon on
        // aurait deja envoye du confidentiel a un service tiers avant de le refuser.
        Assert.DoesNotContain("contentieux-usagers", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", prompt, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // Refus
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_IndexReturnsNothing_RefusesWithoutCallingTheModel()
    {
        var model = new ScriptedLanguageModel("Reponse inventee [horaires-ouverture].");

        var result = await Build(new FakeVectorIndex(), model).ExecuteAsync(Ask("Prix de la piscine ?", Visitor));

        var refused = Assert.IsType<AnswerOutcome.Refused>(result.Outcome);
        Assert.Equal(RefusalReason.NoEvidenceInCorpus, refused.Reason);

        // Sans extrait, un modele repond quand meme — avec aplomb, en puisant dans ses
        // poids. Le refus n'est pas ici une degradation du service : c'est le service.
        Assert.Equal(0, model.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_EverythingFilteredByAccess_RefusesAndTheLanguageModelIsNeverCalled()
    {
        // LE TEST QUI JUSTIFIE LE COMPTEUR D'APPELS DE LA DOUBLURE.
        // Un test qui n'assertait que le refus laisserait passer une implementation qui
        // appelle le modele, ignore sa reponse et refuse quand meme. Ce serait une
        // double faute : on paie l'appel, et surtout on a deja recopie des extraits
        // confidentiels dans le prompt d'un service exterieur.
        var index = new FakeVectorIndex();
        index.SeedScored("grille-remuneration", "Grille de remuneration", AccessLevel.Confidential, 0.97);
        index.SeedScored("contrat-maintenance", "Contrat de maintenance", AccessLevel.Confidential, 0.93);

        var model = new ScriptedLanguageModel("Indice majore [grille-remuneration].");

        var result = await Build(index, model).ExecuteAsync(Ask("Quelle est la remuneration ?", Agent));

        var refused = Assert.IsType<AnswerOutcome.Refused>(result.Outcome);
        Assert.Equal(RefusalReason.NoEvidenceReadableByRequester, refused.Reason);
        Assert.Equal(0, model.CallCount);

        // Le refus « pas pour vous » se distingue du refus « rien dans le corpus » parce
        // que la politique a recu les candidats retenus par le seuil. Les deux
        // explications sont differentes, et cette difference compte pour l'usager.
        Assert.Equal(2, result.Trace.CandidatesFromIndex);
        Assert.Equal(0, result.Trace.CandidatesAfterAccessFilter);
    }

    /// <summary>
    /// LE PIEGE : un fragment lisible mais SOUS LE SEUIL ne doit pas changer le motif du
    /// refus. Ici la reponse est confidentielle, le demandeur est interne, et l'index a
    /// aussi rendu un morceau public sans rapport, trop mal classe pour etre soumis a qui
    /// que ce soit. Le refus reste « rien de lisible » : c'est le controle d'acces qui a
    /// vide la table, pas le modele.
    /// </summary>
    /// <remarks>
    /// CE TEST GARDE UN DEFAUT DEJA VU EN PRODUCTION DE CE DEPOT. En passant a la
    /// politique la sortie BRUTE de l'index plutot que les candidats retenus par le
    /// seuil, ce morceau public de score 0,05 suffisait a lui faire conclure « il restait
    /// du lisible », donc a imputer le refus au modele — un modele que le cas d'usage
    /// venait precisement de ne PAS appeler. La commande affichait « le modele n'a rien
    /// produit » juste sous une trace annoncant « 0 extrait soumis au modele ». Le refus
    /// etait bon, son explication mensongere.
    /// </remarks>
    [Fact]
    public async Task ExecuteAsync_ReadableFragmentBelowThreshold_StillBlamesAccessNotTheModel()
    {
        var index = new FakeVectorIndex();
        index.SeedScored("grille-remuneration", "Grille de remuneration", AccessLevel.Confidential, 0.97);
        index.SeedScored("contrat-maintenance", "Contrat de maintenance", AccessLevel.Confidential, 0.93);
        index.SeedScored("horaires-ouverture", "Horaires d'ouverture au public", AccessLevel.Public, 0.05);

        var model = new ScriptedLanguageModel("Indice majore [grille-remuneration].");

        var result = await Build(index, model).ExecuteAsync(Ask("Quelle est la remuneration ?", Agent));

        var refused = Assert.IsType<AnswerOutcome.Refused>(result.Outcome);
        Assert.Equal(RefusalReason.NoEvidenceReadableByRequester, refused.Reason);
        Assert.Equal(0, model.CallCount);
    }

    /// <summary>
    /// L'AUTRE MOITIE DE LA MEME REGLE. Quand le seuil de score, et non le controle
    /// d'acces, a tout ecarte, il n'y avait rien d'assez proche dans le corpus : le refus
    /// doit le dire. C'est le refus des questions hors sujet, et il ne doit jamais
    /// emprunter les mots du refus d'habilitation — l'usager en deduirait a tort qu'un
    /// document existe et qu'on le lui cache.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_NothingPassesTheScoreThreshold_RefusesForLackOfEvidence()
    {
        var index = new FakeVectorIndex();
        index.SeedScored("horaires-ouverture", "Horaires d'ouverture au public", AccessLevel.Public, 0.06);
        index.SeedScored("espace-jeunesse", "L'espace jeunesse", AccessLevel.Public, 0.04);

        var model = new ScriptedLanguageModel("Le tarif est de 4 euros [horaires-ouverture].");

        var result = await Build(index, model).ExecuteAsync(Ask("Prix de la piscine ?", Visitor));

        var refused = Assert.IsType<AnswerOutcome.Refused>(result.Outcome);
        Assert.Equal(RefusalReason.NoEvidenceInCorpus, refused.Reason);
        Assert.Equal(0, model.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_ModelProducesTextWithoutCitation_Refuses()
    {
        var index = RetardsIndex();
        var model = new ScriptedLanguageModel("L'amende est de dix centimes par jour.");

        var result = await Build(index, model).ExecuteAsync(Ask("Combien coute un retard ?", Visitor));

        var refused = Assert.IsType<AnswerOutcome.Refused>(result.Outcome);

        // REGLE METIER 1, appliquee apres coup sur la sortie du modele. Le prompt le lui
        // demandait ; il ne l'a pas fait ; on ne lui fait pas confiance pour autant.
        Assert.Equal(RefusalReason.ModelProducedNoCitation, refused.Reason);
        Assert.Equal(1, model.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_ModelEmitsTheRefusalMarker_Refuses()
    {
        var index = RetardsIndex();
        var model = new ScriptedLanguageModel(ModelResponseParser.RefusalMarker);

        var result = await Build(index, model).ExecuteAsync(Ask("Combien coute un retard ?", Visitor));

        var refused = Assert.IsType<AnswerOutcome.Refused>(result.Outcome);

        // Le modele a OBEI au gabarit : c'est un refus declare, pas une panne. Le
        // vocabulaire du Domain distingue les deux, sans quoi le systeme ne saurait pas
        // dire si sa regle metier fonctionne ou si son fournisseur est muet.
        Assert.Equal(RefusalReason.ModelDeclinedToAnswer, refused.Reason);
    }

    [Fact]
    public async Task ExecuteAsync_ModelCitesADocumentItWasNeverShown_Refuses()
    {
        // Le modele cite un document confidentiel qui existe dans l'index mais qui a ete
        // ecarte par le controle d'acces : il ne l'a donc jamais vu. Du point de vue de
        // la politique, l'identifiant ne figure dans AUCUN extrait fourni — c'est une
        // citation inventee, et le refus le dit.
        //
        // NOTE ARCHITECTURALE, importante en cours : le motif
        // ModelCitedForbiddenDocument est structurellement INATTEIGNABLE par ce chemin.
        // Le cas d'usage ne soumet a AnswerPolicy que les fragments deja filtres, donc
        // la regle 6 ne peut pas se declencher depuis le pipeline. Elle protege un autre
        // appelant — un cas d'usage futur qui passerait les fragments bruts au juge.
        // C'est une redondance assumee : la politique du Domain doit rester correcte
        // independamment de la prudence de ses appelants.
        var index = RetardsIndex();
        var model = new ScriptedLanguageModel("Le seuil est de 80 euros [contentieux-usagers].");

        var result = await Build(index, model).ExecuteAsync(Ask("Quel est le seuil de saisine ?", Agent));

        var refused = Assert.IsType<AnswerOutcome.Refused>(result.Outcome);
        Assert.Equal(RefusalReason.ModelCitedUnknownDocument, refused.Reason);
    }

    [Fact]
    public void Decide_ForbiddenDocumentCitedAmongSuppliedFragments_RefusesWithTheDedicatedReason()
    {
        // Le pendant du test precedent, appele directement sur la politique : quand les
        // fragments interdits FONT partie du materiau soumis, le refus change de motif.
        // Deux causes distinctes, deux explications distinctes pour l'usager.
        var supplied = new[]
        {
            new EvidenceFragment(DocumentId.From("retards-amendes"), "Retards", "Dix centimes.", AccessLevel.Public, 0),
            new EvidenceFragment(DocumentId.From("contentieux-usagers"), "Contentieux", "Seuil de 80 euros.", AccessLevel.Confidential, 0),
        };

        var outcome = AnswerPolicy.Decide(
            Question.From("Quel est le seuil de saisine ?"),
            Agent,
            supplied,
            new DraftAnswer("Le seuil est de 80 euros [contentieux-usagers].", new[] { DocumentId.From("contentieux-usagers") }));

        var refused = Assert.IsType<AnswerOutcome.Refused>(outcome);
        Assert.Equal(RefusalReason.ModelCitedForbiddenDocument, refused.Reason);
    }

    [Fact]
    public async Task ExecuteAsync_ModelInventsAnIdentifier_Refuses()
    {
        var index = RetardsIndex();
        var model = new ScriptedLanguageModel("Voir le reglement [reglement-interieur-2019].");

        var result = await Build(index, model).ExecuteAsync(Ask("Combien coute un retard ?", Visitor));

        var refused = Assert.IsType<AnswerOutcome.Refused>(result.Outcome);
        Assert.Equal(RefusalReason.ModelCitedUnknownDocument, refused.Reason);
    }

    // -----------------------------------------------------------------------
    // TENSION 1 : le non-determinisme doit rester visible et pilotable
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_TemperatureSeedAndMaxTokens_AreForwardedToTheLanguageModel()
    {
        var index = RetardsIndex();
        var model = new ScriptedLanguageModel("Reponse [retards-amendes].");

        var options = PipelineOptions.Default with
        {
            TopK = 3,
            MinScore = 0.20,
            Temperature = 0.7,
            Seed = 1234,
            MaxTokens = 99,
        };

        await Build(index, model, options).ExecuteAsync(Ask("Combien coute un retard ?", Visitor));

        // Si ces trois valeurs pouvaient se perdre en route, le reglage affiche dans
        // l'empreinte de configuration mentirait — et une derive attribuee a la
        // temperature serait imputee au mauvais coupable.
        var request = model.LastRequest!;
        Assert.Equal(0.7, request.Temperature);
        Assert.Equal(1234, request.Seed);
        Assert.Equal(99, request.MaxTokens);
    }

    [Fact]
    public async Task ExecuteAsync_NullSeed_IsForwardedAsNull()
    {
        var index = RetardsIndex();
        var model = new ScriptedLanguageModel("Reponse [retards-amendes].");

        await Build(index, model, PipelineOptions.Default with { TopK = 3, Seed = null })
            .ExecuteAsync(Ask("Combien coute un retard ?", Visitor));

        Assert.Null(model.LastRequest!.Seed);
    }

    // -----------------------------------------------------------------------
    // TENSION 2 : la panne silencieuse du modele d'embeddings
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_IndexBuiltWithAnotherEmbeddingModel_ReturnsAWarningButStillAnswers()
    {
        var index = RetardsIndex();
        index.Metadata = new IndexMetadata("qwen3-embedding:0.6b", 2, "paragraph", 3, FakeVectorIndex.BuiltAtStub);

        var model = new ScriptedLanguageModel("Reponse [retards-amendes].");

        var result = await Build(index, model, embeddings: new AxisEmbeddingService("bge-m3"))
            .ExecuteAsync(Ask("Combien coute un retard ?", Visitor));

        // Par defaut, le systeme REPOND quand meme. C'est le comportement laxiste que le
        // cours veut montrer : les dimensions concordent, la recherche fonctionne, les
        // scores paraissent normaux — et pourtant les vecteurs viennent de deux espaces
        // qui n'ont rien a voir. Seul l'avertissement rend la panne visible.
        Assert.True(result.Outcome.IsAnswered);
        Assert.NotNull(result.Trace.IndexModelWarning);
        Assert.Contains("qwen3-embedding:0.6b", result.Trace.IndexModelWarning!, StringComparison.Ordinal);
        Assert.Contains("bge-m3", result.Trace.IndexModelWarning!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_StrictIndexModelCheckAndMismatch_ThrowsBeforeTouchingTheModel()
    {
        var index = RetardsIndex();
        index.Metadata = new IndexMetadata("qwen3-embedding:0.6b", 2, "paragraph", 3, FakeVectorIndex.BuiltAtStub);

        var model = new ScriptedLanguageModel("Reponse [retards-amendes].");
        var embeddings = new AxisEmbeddingService("bge-m3");

        var useCase = Build(
            index,
            model,
            PipelineOptions.Default with { TopK = 3, StrictIndexModelCheck = true },
            embeddings);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => useCase.ExecuteAsync(Ask("Combien coute un retard ?", Visitor)));

        Assert.Contains("qwen3-embedding:0.6b", error.Message, StringComparison.Ordinal);

        // La verification est en TETE de pipeline : on n'a ni encode la question, ni
        // interroge l'index, ni appele le modele. Echouer tot coute moins cher que
        // repondre faux.
        Assert.Equal(0, embeddings.EmbedQueryCallCount);
        Assert.Equal(0, model.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_SameEmbeddingModelAsTheIndex_ProducesNoWarning()
    {
        var index = RetardsIndex();
        index.Metadata = new IndexMetadata("axis-fake", 2, "paragraph", 3, FakeVectorIndex.BuiltAtStub);

        var result = await Build(index, new ScriptedLanguageModel("Reponse [retards-amendes]."))
            .ExecuteAsync(Ask("Combien coute un retard ?", Visitor));

        Assert.Null(result.Trace.IndexModelWarning);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyIndexMetadata_IsNotTreatedAsAModelMismatch()
    {
        // Un index jamais construit n'est pas une incoherence de modele : c'est un index
        // vide. Le refus viendra de la politique, avec le bon motif.
        var result = await Build(new FakeVectorIndex(), new ScriptedLanguageModel("x"))
            .ExecuteAsync(Ask("Combien coute un retard ?", Visitor));

        Assert.Null(result.Trace.IndexModelWarning);
    }

    // -----------------------------------------------------------------------
    // Configuration et garde-fous
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_InvalidOptions_FailFastBeforeAnyPortIsCalled()
    {
        var index = RetardsIndex();
        var model = new ScriptedLanguageModel("Reponse [retards-amendes].");

        var useCase = Build(index, model, PipelineOptions.Default with { TopK = 0 });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => useCase.ExecuteAsync(Ask("Combien coute un retard ?", Visitor)));

        Assert.Equal(0, model.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_Trace_CarriesTheFullConfigurationFingerprint()
    {
        var index = RetardsIndex();
        index.Metadata = new IndexMetadata("axis-fake", 2, "paragraph", 3, FakeVectorIndex.BuiltAtStub);

        var options = PipelineOptions.Default with { TopK = 3, MinScore = 0.20, Temperature = 0.3, Seed = 7 };
        var model = new ScriptedLanguageModel("Reponse [retards-amendes].", "granite4.2:3b");

        var result = await Build(index, model, options).ExecuteAsync(Ask("Combien coute un retard ?", Visitor));

        var configuration = result.Trace.Configuration;

        Assert.Equal("axis-fake", configuration.EmbeddingModel);
        Assert.Equal(2, configuration.Dimension);
        Assert.Equal("paragraph", configuration.ChunkingStrategyId);
        Assert.Equal(AccessFilterMode.Post, configuration.FilterMode);
        Assert.Equal(3, configuration.TopK);
        Assert.Equal("granite4.2:3b", configuration.LlmModelId);
        Assert.Equal(0.3, configuration.Temperature);
        Assert.Equal(7, configuration.Seed);
        Assert.Equal(FakePromptCatalog.DefaultTemplate.Fingerprint, configuration.PromptFingerprint);
    }

    [Fact]
    public async Task ExecuteAsync_PreFilterMode_SendsTheClearanceAndAnswersWithMoreEvidence()
    {
        // Le meme corpus, la meme question, le meme agent : seul le mode change. Deux
        // extraits en post-filtrage, trois en pre-filtrage, parce que le confidentiel
        // n'a pas pris de place dans le classement.
        var post = await Build(RetardsIndex(), new ScriptedLanguageModel("Reponse [retards-amendes]."))
            .ExecuteAsync(Ask("Combien de relances ?", Agent));

        var preIndex = RetardsIndex();
        var pre = await Build(
                preIndex,
                new ScriptedLanguageModel("Reponse [retards-amendes]."),
                PipelineOptions.Default with { TopK = 3, MinScore = 0.20, FilterMode = AccessFilterMode.Pre })
            .ExecuteAsync(Ask("Combien de relances ?", Agent));

        Assert.Equal(2, post.Trace.CandidatesAfterAccessFilter);
        Assert.Equal(2, pre.Trace.CandidatesAfterAccessFilter);
        Assert.Equal(SearchFilter.UpTo(AccessLevel.Internal), preIndex.LastFilter);

        // En pre-filtrage rien n'est jete apres coup : l'index n'a jamais propose
        // d'interdit, donc la trace ne montre aucune perte due a l'acces.
        Assert.Empty(pre.Trace.DroppedByAccessFilter);
        Assert.Single(post.Trace.DroppedByAccessFilter);
    }

    [Fact]
    public async Task ExecuteAsync_NullCommand_Throws()
    {
        var useCase = Build(RetardsIndex(), new ScriptedLanguageModel("x"));

        await Assert.ThrowsAsync<ArgumentNullException>(() => useCase.ExecuteAsync(null!));
    }

    [Fact]
    public void Constructor_NullPort_Throws()
    {
        var index = new FakeVectorIndex();
        var options = PipelineOptions.Default;

        Assert.Throws<ArgumentNullException>(
            () => new AnswerQuestionUseCase(null!, index, new ScriptedLanguageModel("x"), FakePromptCatalog.Default(), options));
        Assert.Throws<ArgumentNullException>(
            () => new AnswerQuestionUseCase(new AxisEmbeddingService(), index, null!, FakePromptCatalog.Default(), options));
    }
}
