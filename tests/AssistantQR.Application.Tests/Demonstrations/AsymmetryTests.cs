// ---------------------------------------------------------------------------
// TESTS DE DEMONSTRATION — support de cours
//
// Les autres fichiers de cette suite verifient que le code fait ce qu'il promet.
// Celui-ci ne verifie pas : il MONTRE. Chaque test etablit une propriete du systeme
// que l'on peut affirmer en amphi et qui, ici, s'execute en quelques millisecondes.
//
// La these est une ASYMETRIE que l'intuition inverse presque toujours :
//
//   — Le modele de langue, composant le plus visible, le plus cher, le plus discute,
//     est un DETAIL REMPLACABLE. On le change, la recherche ne bouge pas d'un iota.
//   — Le modele d'embeddings, choisi une fois au debut du projet et jamais reexamine,
//     est un ENGAGEMENT STRUCTUREL. On le change, tout change — et sans la moindre
//     erreur pour prevenir.
//   — Le decoupage en morceaux, reglage qui ressemble a une preference de mise en
//     forme, determine ce que le modele voit, donc ce qu'il repond.
//
// Autrement dit : les composants dont on parle le plus sont ceux qui comptent le
// moins, et reciproquement. Toute la couche Application est structuree pour rendre
// cette asymetrie visible plutot que subie.
// ---------------------------------------------------------------------------

using AssistantQR.Application.Configuration;
using AssistantQR.Application.Model;
using AssistantQR.Application.Ports;
using AssistantQR.Application.Tests.Doubles;
using AssistantQR.Application.UseCases.AnswerQuestion;
using AssistantQR.Application.UseCases.IndexCorpus;

using AssistantQR.Domain.Access;
using AssistantQR.Domain.Answers;
using AssistantQR.Domain.Documents;
using AssistantQR.Domain.Questions;

using Xunit;

namespace AssistantQR.Application.Tests.Demonstrations;

/// <summary>
/// Les trois demonstrations executables du cours. Voir l'en-tete du fichier.
/// </summary>
public sealed class AsymmetryTests
{
    private const string QuestionText = "Quels sont les horaires d'ouverture le samedi ?";

    private static readonly Requester Visitor = Requester.Create("visiteur", "public");

    private static readonly PipelineOptions Options =
        PipelineOptions.Default with { TopK = 3, MinScore = 0d };

    /// <summary>
    /// Un corpus miniature calque sur celui du projet : sujet unique, vocabulaire qui
    /// se recoupe d'un document a l'autre. Ce recouvrement n'est pas une facilite, c'est
    /// la condition meme des demonstrations : si chaque document parlait d'un sujet
    /// disjoint, n'importe quel modele d'embeddings retrouverait le bon et l'on ne
    /// verrait jamais que le choix du modele engage a quelque chose.
    /// </summary>
    private static FakeDocumentRepository Corpus() => new(
        FakeDocumentRepository.Doc("horaires-ouverture", "Horaires d'ouverture au public", AccessLevel.Public,
            "La mediatheque accueille le public du mardi au samedi.\n\n" +
            "Le samedi, l'ouverture est continue de 10 h a 18 h sans interruption.\n\n" +
            "Le dimanche et le lundi, l'etablissement reste ferme au public."),
        FakeDocumentRepository.Doc("espace-jeunesse", "L'espace jeunesse", AccessLevel.Public,
            "L'espace jeunesse occupe le premier etage de la mediatheque.\n\n" +
            "Il ouvre le samedi matin des 10 h pour l'heure du conte.\n\n" +
            "Les enfants de moins de huit ans doivent etre accompagnes."),
        FakeDocumentRepository.Doc("salles-de-travail", "Reservation des salles de travail", AccessLevel.Public,
            "Quatre salles de travail sont reservables en ligne.\n\n" +
            "La reservation du samedi ouvre le mardi precedent a 9 h.\n\n" +
            "Une salle est bloquee au maximum trois heures consecutives."),
        FakeDocumentRepository.Doc("pret-documents", "Regles de pret et de retour", AccessLevel.Public,
            "Le pret courant porte sur dix documents pour vingt et un jours.\n\n" +
            "La boite de retour reste accessible le samedi apres la fermeture.\n\n" +
            "Les documents patrimoniaux sont exclus du pret."),
        FakeDocumentRepository.Doc("programme-animations", "Programme des animations culturelles", AccessLevel.Public,
            "Les animations se tiennent principalement le mercredi et le samedi.\n\n" +
            "Le programme du trimestre est affiche dans le hall d'accueil.\n\n" +
            "L'entree des animations est libre et gratuite."),
        FakeDocumentRepository.Doc("acces-wifi-postes", "Acces Wi-Fi et postes informatiques", AccessLevel.Public,
            "Douze postes informatiques sont a disposition du public.\n\n" +
            "Le Wi-Fi est accessible aux heures d'ouverture, samedi compris.\n\n" +
            "Une session dure une heure, renouvelable selon l'affluence."));

    private static async Task<FakeVectorIndex> BuildIndexAsync(
        IEmbeddingService embeddings,
        IChunkingStrategy chunking)
    {
        var index = new FakeVectorIndex();
        await new IndexCorpusUseCase(Corpus(), chunking, embeddings, index).ExecuteAsync();
        return index;
    }

    private static AnswerQuestionUseCase Assistant(
        IEmbeddingService embeddings,
        FakeVectorIndex index,
        ILanguageModel languageModel,
        PipelineOptions? options = null) =>
        new(embeddings, index, languageModel, FakePromptCatalog.Default(), options ?? Options);

    private static IReadOnlyList<string> RetrievedIds(AnswerQuestionResult result)
    {
        var ids = new List<string>(result.Trace.Supplied.Count);
        foreach (var scored in result.Trace.Supplied)
        {
            ids.Add(scored.Fragment.DocumentId.Value);
        }

        return ids;
    }

    private static IReadOnlyList<string> CitedIds(AnswerQuestionResult result)
    {
        var answer = result.Outcome.AnswerOrNull;
        if (answer is null)
        {
            return Array.Empty<string>();
        }

        var ids = new List<string>(answer.CitedDocumentIds.Count);
        foreach (var id in answer.CitedDocumentIds)
        {
            ids.Add(id.Value);
        }

        return ids;
    }

    // =======================================================================
    // DEMONSTRATION 1 — Le modele de langue est un detail remplacable
    // =======================================================================

    [Fact]
    public async Task SwappingLanguageModel_DoesNotChangeRetrieval()
    {
        var embeddings = new FakeEmbeddingService("modele-partage", 24);
        var index = await BuildIndexAsync(embeddings, new ParagraphChunkingDouble());

        // Deux modeles de langue aussi differents que possible : l'un recite une phrase
        // ecrite d'avance, l'autre relit les extraits et les recopie. Rien d'autre ne
        // change : meme index, meme service d'embeddings, meme question, meme demandeur.
        var scripted = new ScriptedLanguageModel("Ouverture continue le samedi [horaires-ouverture].", "modele-alpha");
        var echoing = new EchoingLanguageModel("modele-beta");

        var first = await Assistant(embeddings, index, scripted)
            .ExecuteAsync(new AnswerQuestionCommand(Question.From(QuestionText), Visitor));
        var second = await Assistant(embeddings, index, echoing)
            .ExecuteAsync(new AnswerQuestionCommand(Question.From(QuestionText), Visitor));

        // LES FRAGMENTS RECUPERES SONT IDENTIQUES — memes documents, memes rangs, memes
        // scores au bit pres. Le modele de langue intervient APRES la recherche et n'a
        // aucun moyen de l'influencer : il est le dernier maillon, pas le premier.
        Assert.NotEmpty(first.Trace.Supplied);
        Assert.Equal(RetrievedIds(first), RetrievedIds(second));
        Assert.Equal(first.Trace.Supplied.Count, second.Trace.Supplied.Count);

        for (var i = 0; i < first.Trace.Supplied.Count; i++)
        {
            Assert.Equal(first.Trace.Supplied[i].Fragment, second.Trace.Supplied[i].Fragment);
            Assert.Equal(first.Trace.Supplied[i].Score, second.Trace.Supplied[i].Score);
        }

        // Les REPONSES, elles, different : c'est bien le meme systeme avec un autre
        // redacteur. On a change de fournisseur sans toucher a une ligne du pipeline,
        // et sans que la partie difficile — trouver les bons extraits — ne bouge.
        Assert.NotEqual(
            first.Outcome.AnswerOrNull?.Text,
            second.Outcome.AnswerOrNull?.Text);

        // Corollaire pratique, souvent contre-intuitif : changer de LLM ne repare pas une
        // mauvaise recherche. Si les extraits fournis sont hors sujet, le meilleur modele
        // du monde produira une reponse hors sujet — ou refusera, ce qui est deja mieux.
        Assert.Equal(1, scripted.CallCount);
        Assert.Equal(1, echoing.CallCount);
    }

    // =======================================================================
    // DEMONSTRATION 2 — Le modele d'embeddings est un engagement structurel
    // =======================================================================

    [Fact]
    public async Task SwappingEmbeddingModel_ChangesRetrieval_WithoutAnyError()
    {
        // Deux modeles d'embeddings de MEME DIMENSION et de fonction de projection
        // differente. C'est le cas realiste : 768 et 1024 sont des tailles banales,
        // partagees par des dizaines de modeles incompatibles entre eux.
        var indexingModel = new FakeEmbeddingService("modele-d-indexation", 24, salt: 0u);
        var queryingModel = new FakeEmbeddingService("modele-d-interrogation", 24, salt: 2654435761u);

        var index = await BuildIndexAsync(indexingModel, new ParagraphChunkingDouble());

        var command = new AnswerQuestionCommand(Question.From(QuestionText), Visitor);

        var coherent = await Assistant(indexingModel, index, new EchoingLanguageModel()).ExecuteAsync(command);
        var incoherent = await Assistant(queryingModel, index, new EchoingLanguageModel()).ExecuteAsync(command);

        // --- 1. AUCUNE ERREUR. Pas d'exception, pas de dimension incompatible, pas de
        // refus technique. Le systeme repond, avec des scores d'apparence normale.
        Assert.NotEmpty(incoherent.Trace.Supplied);
        Assert.All(incoherent.Trace.Supplied, scored => Assert.InRange(scored.Score, -1d, 1d));

        // --- 2. ET POURTANT LES DOCUMENTS RECUPERES DIFFERENT. Les vecteurs de l'index
        // vivent dans l'espace du premier modele, la question dans celui du second. Les
        // deux espaces n'ont aucun rapport ; le cosinus calcule entre eux est du bruit
        // parfaitement bien forme.
        Assert.NotEqual(RetrievedIds(coherent), RetrievedIds(incoherent));

        // Et pas d'un cheveu : le document le mieux classe n'est plus le meme. Sur ce
        // corpus, le modele coherent place « horaires-ouverture » en tete ; le modele
        // incoherent remonte un document qui ne parle pas du sujet et relegue le bon en
        // troisieme position. La reponse produite sera sourcee, plausible, et fausse.
        Assert.NotEqual(RetrievedIds(coherent)[0], RetrievedIds(incoherent)[0]);

        // C'est LA PANNE SILENCIEUSE. Rien ne casse, rien ne compile mal, rien ne leve.
        // Le systeme continue de repondre en citant ses sources — des sources choisies
        // au hasard. Un test unitaire ne la voit pas ; une suite d'integration qui
        // n'assertait que « une reponse a ete produite » ne la voit pas non plus.
        // Ce qui la rend visible, c'est la comparaison d'instantanes.

        // --- 3. LE SEUL GARDE-FOU : l'index sait avec quel modele il a ete construit,
        // et le cas d'usage compare les deux noms.
        Assert.Null(coherent.Trace.IndexModelWarning);
        Assert.NotNull(incoherent.Trace.IndexModelWarning);
        Assert.Contains("modele-d-indexation", incoherent.Trace.IndexModelWarning!, StringComparison.Ordinal);
        Assert.Contains("modele-d-interrogation", incoherent.Trace.IndexModelWarning!, StringComparison.Ordinal);

        // --- 4. LES DEUX MONDES. Le depot montre le comportement laxiste (defaut
        // pedagogique, pour que la panne soit reproductible) ET le comportement
        // raisonnable en production : on refuse d'interroger un index etranger.
        var strict = Assistant(
            queryingModel,
            index,
            new EchoingLanguageModel(),
            Options with { StrictIndexModelCheck = true });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => strict.ExecuteAsync(command));
        Assert.Contains("modele-d-indexation", error.Message, StringComparison.Ordinal);

        // Morale : le choix du modele d'embeddings n'est pas un reglage, c'est un
        // engagement. En changer impose de reindexer TOUT le corpus. Le modele de langue,
        // lui, se remplace entre deux requetes (demonstration 1). L'intuition classe ces
        // deux decisions dans l'ordre inverse.
    }

    // =======================================================================
    // DEMONSTRATION 3 — CACE : changer le decoupage change les reponses
    // =======================================================================

    [Fact]
    public async Task ChangingChunkingStrategy_ChangesEveryAnswer()
    {
        // Meme corpus, meme modele d'embeddings, meme prompt, meme modele de langue,
        // meme question, meme demandeur. UNE seule chose change : la facon de decouper
        // les documents avant de les indexer. Aucune regle metier n'est concernee — le
        // Domain ignore jusqu'a l'existence du decoupage.
        var embeddings = new FakeEmbeddingService("modele-partage", 24);

        var byParagraph = await BuildIndexAsync(embeddings, new ParagraphChunkingDouble());
        var byDocument = await BuildIndexAsync(embeddings, new WholeDocumentChunkingDouble());

        var command = new AnswerQuestionCommand(Question.From(QuestionText), Visitor);

        var fine = await Assistant(embeddings, byParagraph, new EchoingLanguageModel()).ExecuteAsync(command);
        var coarse = await Assistant(embeddings, byDocument, new EchoingLanguageModel()).ExecuteAsync(command);

        // Le decoupage fin isole le paragraphe qui repond ; le decoupage grossier le noie
        // dans le reste du document et deplace tous les scores. Les extraits retenus
        // changent, donc les sources citees changent, donc la reponse change.
        Assert.NotEqual(RetrievedIds(fine), RetrievedIds(coarse));
        Assert.NotEqual(CitedIds(fine), CitedIds(coarse));

        // C'est le principe CACE — Changing Anything Changes Everything. Il n'a rien
        // d'une fatalite : il dit seulement qu'un systeme dont les composants
        // interagissent de facon non lineaire n'a pas de reglage « local ». La reponse
        // architecturale n'est pas de figer le decoupage, c'est de le rendre NOMME,
        // VERSIONNE et ENREGISTRE — pour qu'une derive constatee trois mois plus tard
        // soit attribuable au lieu d'etre mysterieuse.
        Assert.Equal("fake-paragraph", fine.Trace.Configuration.ChunkingStrategyId);
        Assert.Equal("fake-whole-document", coarse.Trace.Configuration.ChunkingStrategyId);

        var differences = fine.Trace.Configuration.DifferencesWith(coarse.Trace.Configuration);
        Assert.Contains(differences, d => d.StartsWith("ChunkingStrategyId", StringComparison.Ordinal));

        // Et la contrepartie rassurante : quoi qu'il arrive au decoupage, les deux
        // reponses restent SOURCEES. La regle metier tient, parce qu'elle n'est pas dans
        // le pipeline mais dans un type du Domain qu'aucun reglage ne traverse.
        AssertAnsweredWithSources(fine);
        AssertAnsweredWithSources(coarse);
    }

    private static void AssertAnsweredWithSources(AnswerQuestionResult result)
    {
        var answered = Assert.IsType<AnswerOutcome.Answered>(result.Outcome);
        Assert.NotEmpty(answered.Answer.Citations);
    }
}
