using AssistantQR.Domain.Access;
using AssistantQR.Domain.Answers;
using AssistantQR.Domain.Documents;
using AssistantQR.Domain.Evidence;
using AssistantQR.Domain.Policies;
using AssistantQR.Domain.Questions;
using Xunit;

namespace AssistantQR.Domain.Tests;

/// <summary>
/// Le fichier de test le plus important du depot, parce qu'il teste la fonction la
/// plus importante du depot. AnswerPolicy.Decide est le juge : la sortie du modele de
/// langue n'y entre que comme une PROPOSITION. Aucune cle d'API, aucun reseau, aucune
/// horloge n'est necessaire pour executer ce fichier — ce qui veut dire que la partie
/// du systeme dont la justesse est verifiable est ici verifiee exhaustivement, en
/// quelques millisecondes, et de facon reproductible. Le modele, lui, ne le sera jamais.
///
/// Les huit regles sont evaluees dans un ordre fixe. Un test par branche, plus les
/// tests d'ordre qui montrent quelle regle l'emporte quand deux echouent en meme temps.
/// </summary>
public sealed class AnswerPolicyTests
{
    private static readonly Question Sujet = Question.From("Que se passe-t-il en cas de retard ?");

    private static readonly Requester Visiteur = Requester.Create("visiteur", "public");
    private static readonly Requester Agent = Requester.Create("agent", "internal");
    private static readonly Requester Direction = Requester.Create("direction", "confidential");

    private static EvidenceFragment Fragment(
        string id,
        AccessLevel level,
        int ordinal = 0,
        string? text = null) =>
        new(DocumentId.From(id), $"Titre de {id}", text ?? $"Texte de {id}, fragment {ordinal}.", level, ordinal);

    private static DraftAnswer Draft(string text, params string[] citedIds) =>
        new(text, citedIds.Select(DocumentId.From).ToList());

    private static AnswerOutcome.Refused AssertRefused(AnswerOutcome outcome, RefusalReason expected)
    {
        var refused = Assert.IsType<AnswerOutcome.Refused>(outcome);

        Assert.Equal(expected, refused.Reason);
        Assert.False(outcome.IsAnswered);
        Assert.Null(outcome.AnswerOrNull);
        Assert.False(string.IsNullOrWhiteSpace(refused.Explanation));

        return refused;
    }

    // =====================================================================
    // REGLE 1 — aucun extrait fourni
    // =====================================================================

    [Fact]
    public void Decide_AucunExtraitFourni_RefuseNoEvidenceInCorpus()
    {
        var outcome = AnswerPolicy.Decide(
            Sujet, Visiteur, Array.Empty<EvidenceFragment>(), Draft("Une réponse.", "retards-amendes"));

        AssertRefused(outcome, RefusalReason.NoEvidenceInCorpus);
    }

    [Fact]
    public void Decide_ListeDExtraitsNulle_RefuseNoEvidenceInCorpus()
    {
        var outcome = AnswerPolicy.Decide(Sujet, Visiteur, null!, DraftAnswer.Empty);

        AssertRefused(outcome, RefusalReason.NoEvidenceInCorpus);
    }

    /// <summary>
    /// Ordre : « rien trouve » l'emporte sur « le modele n'a rien dit ». C'est le bon
    /// ordre pedagogiquement — la cause premiere est en amont du modele.
    /// </summary>
    [Fact]
    public void Decide_AucunExtraitEtBrouillonVide_LaCauseRetenueEstLAbsenceDExtraits()
    {
        var outcome = AnswerPolicy.Decide(
            Sujet, Visiteur, Array.Empty<EvidenceFragment>(), DraftAnswer.Empty);

        AssertRefused(outcome, RefusalReason.NoEvidenceInCorpus);
    }

    // =====================================================================
    // REGLE 2 — des extraits existent, mais aucun n'est lisible
    // =====================================================================

    /// <summary>
    /// Refus different du precedent, et la difference compte : ici le systeme SAIT,
    /// mais ne dira pas. L'explication ne doit surtout pas divulguer le contenu.
    /// </summary>
    [Fact]
    public void Decide_ExtraitsTousHorsHabilitation_RefuseNoEvidenceReadableByRequester()
    {
        var fragments = new[]
        {
            Fragment("gestion-retards-interne", AccessLevel.Internal),
            Fragment("contentieux-usagers", AccessLevel.Confidential),
        };

        var outcome = AnswerPolicy.Decide(
            Sujet, Visiteur, fragments, Draft("Une réponse.", "gestion-retards-interne"));

        var refused = AssertRefused(outcome, RefusalReason.NoEvidenceReadableByRequester);
        Assert.Contains("public", refused.Explanation);
        Assert.DoesNotContain("Texte de gestion-retards-interne", refused.Explanation);
    }

    [Fact]
    public void Decide_UnSeulExtraitLisibleParmiDesInterdits_NeRefusePasPourHabilitation()
    {
        var fragments = new[]
        {
            Fragment("contentieux-usagers", AccessLevel.Confidential),
            Fragment("retards-amendes", AccessLevel.Public),
        };

        var outcome = AnswerPolicy.Decide(
            Sujet, Visiteur, fragments, Draft("Un retard entraîne une relance.", "retards-amendes"));

        Assert.True(outcome.IsAnswered);
    }

    // =====================================================================
    // REGLE 3 — le modele a DECLARE ne pas pouvoir repondre
    // =====================================================================

    /// <summary>
    /// Obeir n'est pas tomber en panne. Le gabarit de prompt ordonne au modele d'emettre
    /// le marqueur de refus quand les extraits ne suffisent pas ; quand il le fait, le
    /// Domain doit le dire avec le vocabulaire de la regle metier, pas avec celui de
    /// l'incident technique.
    /// </summary>
    [Fact]
    public void Decide_RefusDeclareParLeModele_RefuseModelDeclinedToAnswer()
    {
        var fragments = new[] { Fragment("retards-amendes", AccessLevel.Public) };

        var outcome = AnswerPolicy.Decide(Sujet, Visiteur, fragments, DraftAnswer.DeclinedByModel);

        var refused = AssertRefused(outcome, RefusalReason.ModelDeclinedToAnswer);

        Assert.DoesNotContain("aucun texte exploitable", refused.Explanation, StringComparison.Ordinal);
    }

    /// <summary>
    /// Un refus declare qui porterait quand meme du texte reste un refus declare : le
    /// drapeau prime sur le contenu, sinon la declaration serait annulee par le bavardage.
    /// </summary>
    [Fact]
    public void Decide_RefusDeclareAvecDuTexte_RefuseQuandMemeModelDeclinedToAnswer()
    {
        var fragments = new[] { Fragment("retards-amendes", AccessLevel.Public) };
        var draft = new DraftAnswer(
            "AUCUNE_REPONSE",
            new[] { DocumentId.From("retards-amendes") },
            Declined: true);

        var outcome = AnswerPolicy.Decide(Sujet, Visiteur, fragments, draft);

        AssertRefused(outcome, RefusalReason.ModelDeclinedToAnswer);
    }

    /// <summary>
    /// PRIORITE DES REGLES. Si aucun extrait n'etait lisible, c'est le controle d'acces
    /// qui a tranche — le modele n'a meme pas ete appele. Imputer ce refus au modele
    /// serait un mensonge sur la cause, et masquerait une decision de securite derriere
    /// une decision de generation.
    /// </summary>
    [Fact]
    public void Decide_RefusDeclareMaisAucunExtraitLisible_LaCauseRetenueEstLAcces()
    {
        var fragments = new[] { Fragment("contentieux-usagers", AccessLevel.Confidential) };

        var outcome = AnswerPolicy.Decide(Sujet, Visiteur, fragments, DraftAnswer.DeclinedByModel);

        AssertRefused(outcome, RefusalReason.NoEvidenceReadableByRequester);
    }

    /// <summary>
    /// Meme priorite, un cran plus haut : sans le moindre extrait, la question sort du
    /// corpus, et cela se dit avant toute consideration sur ce qu'a fait le modele.
    /// </summary>
    [Fact]
    public void Decide_RefusDeclareMaisAucunExtraitDuTout_LaCauseRetenueEstLeCorpus()
    {
        var outcome = AnswerPolicy.Decide(
            Sujet, Visiteur, Array.Empty<EvidenceFragment>(), DraftAnswer.DeclinedByModel);

        AssertRefused(outcome, RefusalReason.NoEvidenceInCorpus);
    }

    // =====================================================================
    // REGLE 4 — le modele n'a rien produit d'exploitable
    // =====================================================================

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t  ")]
    public void Decide_TexteDuModeleVideOuBlanc_RefuseModelProducedEmptyAnswer(string text)
    {
        var fragments = new[] { Fragment("retards-amendes", AccessLevel.Public) };

        var outcome = AnswerPolicy.Decide(Sujet, Visiteur, fragments, Draft(text, "retards-amendes"));

        AssertRefused(outcome, RefusalReason.ModelProducedEmptyAnswer);
    }

    [Fact]
    public void Decide_BrouillonNul_RefuseModelProducedEmptyAnswer()
    {
        var fragments = new[] { Fragment("retards-amendes", AccessLevel.Public) };

        var outcome = AnswerPolicy.Decide(Sujet, Visiteur, fragments, null!);

        AssertRefused(outcome, RefusalReason.ModelProducedEmptyAnswer);
    }

    /// <summary>
    /// Le pendant du test precedent : un texte vide SANS declaration reste un incident
    /// technique. C'est la seule facon de garder les deux evenements mesurables
    /// separement dans les journaux.
    /// </summary>
    [Fact]
    public void Decide_TexteVideSansDeclaration_RefuseModelProducedEmptyAnswer()
    {
        var fragments = new[] { Fragment("retards-amendes", AccessLevel.Public) };
        var draft = new DraftAnswer(string.Empty, Array.Empty<DocumentId>(), Declined: false);

        var outcome = AnswerPolicy.Decide(Sujet, Visiteur, fragments, draft);

        AssertRefused(outcome, RefusalReason.ModelProducedEmptyAnswer);
    }

    /// <summary>
    /// DraftAnswer.Empty est ce que l'Application soumet quand elle n'a MEME PAS appele
    /// le modele. Le refus doit alors porter sur le texte vide, pas sur l'absence de
    /// citation : l'ordre des regles rend la cause exacte lisible dans la trace.
    /// </summary>
    [Fact]
    public void Decide_BrouillonVideEtSansCitation_LaCauseRetenueEstLeTexteVide()
    {
        var fragments = new[] { Fragment("retards-amendes", AccessLevel.Public) };

        var outcome = AnswerPolicy.Decide(Sujet, Visiteur, fragments, DraftAnswer.Empty);

        AssertRefused(outcome, RefusalReason.ModelProducedEmptyAnswer);
    }

    // =====================================================================
    // REGLE 5 — texte sans la moindre citation
    // =====================================================================

    [Fact]
    public void Decide_TexteSansAucuneCitation_RefuseModelProducedNoCitation()
    {
        var fragments = new[] { Fragment("retards-amendes", AccessLevel.Public) };
        var draft = new DraftAnswer(
            "Une réponse parfaitement plausible, parfaitement fluide, et parfaitement invérifiable.",
            Array.Empty<DocumentId>());

        var outcome = AnswerPolicy.Decide(Sujet, Visiteur, fragments, draft);

        AssertRefused(outcome, RefusalReason.ModelProducedNoCitation);
    }

    [Fact]
    public void Decide_ListeDeCitationsNulleDansLeBrouillon_RefuseModelProducedNoCitation()
    {
        var fragments = new[] { Fragment("retards-amendes", AccessLevel.Public) };

        var outcome = AnswerPolicy.Decide(Sujet, Visiteur, fragments, new DraftAnswer("Une réponse.", null!));

        AssertRefused(outcome, RefusalReason.ModelProducedNoCitation);
    }

    // =====================================================================
    // REGLE 6 — citation hallucinee
    // =====================================================================

    [Fact]
    public void Decide_IdentifiantCiteInexistant_RefuseModelCitedUnknownDocument()
    {
        var fragments = new[] { Fragment("retards-amendes", AccessLevel.Public) };

        var outcome = AnswerPolicy.Decide(
            Sujet, Visiteur, fragments, Draft("Une réponse.", "reglement-piscine-municipale"));

        var refused = AssertRefused(outcome, RefusalReason.ModelCitedUnknownDocument);
        Assert.Contains("reglement-piscine-municipale", refused.Explanation);
    }

    /// <summary>
    /// LE cas subtil de la regle 5. « pret-documents » existe bel et bien dans le
    /// corpus de la mediatheque, il est public, et sa citation a l'air impeccable.
    /// Mais il n'a PAS ete fourni en extrait pour cette question-ci : le modele ne
    /// pouvait donc pas le lire, il l'a recite de memoire. Le Domain ne connait pas
    /// le corpus — et c'est precisement ce qui le sauve : sa seule verite est la liste
    /// des extraits reellement transmis. Une citation plausible reste une hallucination
    /// si rien ne l'a etayee ici et maintenant.
    /// </summary>
    [Fact]
    public void Decide_IdentifiantExistantDansLeCorpusMaisNonFourniEnExtrait_RefuseModelCitedUnknownDocument()
    {
        var fragments = new[] { Fragment("retards-amendes", AccessLevel.Public) };

        var outcome = AnswerPolicy.Decide(
            Sujet,
            Visiteur,
            fragments,
            Draft(
                "Un retard entraîne une relance [retards-amendes] et le prêt dure 28 jours [pret-documents].",
                "retards-amendes",
                "pret-documents"));

        var refused = AssertRefused(outcome, RefusalReason.ModelCitedUnknownDocument);
        Assert.Contains("pret-documents", refused.Explanation);
    }

    /// <summary>
    /// Ordre : l'invention l'emporte sur l'acces. Une citation inventee est un defaut
    /// du modele, une citation interdite est un defaut du filtrage ; on nomme d'abord
    /// celui qui rend la réponse infondee.
    /// </summary>
    [Fact]
    public void Decide_UneCitationInventeeEtUneCitationInterdite_LaCauseRetenueEstLInvention()
    {
        var fragments = new[]
        {
            Fragment("retards-amendes", AccessLevel.Public),
            Fragment("contentieux-usagers", AccessLevel.Confidential),
        };

        var outcome = AnswerPolicy.Decide(
            Sujet, Visiteur, fragments, Draft("Une réponse.", "contentieux-usagers", "document-imaginaire"));

        AssertRefused(outcome, RefusalReason.ModelCitedUnknownDocument);
    }

    // =====================================================================
    // REGLE 7 — LE TEST LE PLUS IMPORTANT DU DEPOT
    // =====================================================================

    /// <summary>
    /// ===================================================================
    /// LE TEST LE PLUS IMPORTANT DU DEPOT.
    /// ===================================================================
    /// Scenario du POST-FILTRAGE, celui que tout le cours cherche a rendre visible.
    ///
    /// L'index vectoriel a ete interroge SANS filtre d'acces (mode « post ») : c'est
    /// le mode par defaut, et c'est le plus performant, parce que le filtrage a
    /// posteriori ne degrade pas la qualite du top-K. L'extrait confidentiel
    /// « contentieux-usagers » est donc remonte dans les candidats — et selon la
    /// facon dont le pipeline est cable, il a pu se retrouver dans le prompt.
    /// Le modele l'a lu. Le modele le cite. Sa réponse est fluide, sourcee, et
    /// factuellement exacte. Elle est aussi une divulgation.
    ///
    /// Rien dans la sortie du modele ne signale le probleme : ni sa confiance, ni sa
    /// syntaxe, ni la coherence de son texte. Aucune relecture humaine du texte seul
    /// ne le detecterait de facon fiable. Seule une regle placee EN AVAL du modele,
    /// dans une fonction pure qui compare la citation a l'habilitation du demandeur,
    /// attrape le cas — et le refuse.
    ///
    /// C'est la these du depot en un test : on ne rend pas un systeme d'IA sur en
    /// esperant que le modele se tienne bien ; on le rend sur en placant la decision
    /// finale hors de sa portee, dans du code deterministe et testable. Le modele
    /// propose, le Domain dispose.
    /// </summary>
    [Fact]
    public void Decide_LeModeleCiteUnDocumentInterditQuIlAVuEnPostFiltrage_RefuseModelCitedForbiddenDocument()
    {
        // Ce que l'index a remonte : un document public et un document confidentiel,
        // parce qu'en mode post-filtrage la recherche ignore l'habilitation.
        var candidatsRemontesParLIndex = new[]
        {
            Fragment(
                "retards-amendes",
                AccessLevel.Public,
                text: "Passé quinze jours, une amende de 0,20 € par jour et par document est appliquée."),
            Fragment(
                "contentieux-usagers",
                AccessLevel.Confidential,
                text: "Madame Berthier, carte n° 4417, doit 84 € et fait l'objet d'un recouvrement."),
        };

        // Le demandeur est un agent : habilite « interne », donc PAS « confidentiel ».
        // La reponse du modele est irreprochable dans sa forme, et cite ses deux sources.
        var propositionDuModele = Draft(
            "Une amende de 0,20 € par jour s'applique [retards-amendes] ; Madame Berthier doit "
            + "actuellement 84 € et fait l'objet d'un recouvrement [contentieux-usagers].",
            "retards-amendes",
            "contentieux-usagers");

        var outcome = AnswerPolicy.Decide(Sujet, Agent, candidatsRemontesParLIndex, propositionDuModele);

        var refused = AssertRefused(outcome, RefusalReason.ModelCitedForbiddenDocument);

        // Le refus nomme le document fautif et l'habilitation du demandeur : sans cela,
        // l'incident serait indiagnosticable en production.
        Assert.Contains("contentieux-usagers", refused.Explanation);
        Assert.Contains("internal", refused.Explanation);

        // Et surtout : rien de la reponse du modele ne sort. Pas de reponse partielle,
        // pas de « voici ce que j'ai pu dire ». Une divulgation partielle reste une
        // divulgation.
        Assert.Null(outcome.AnswerOrNull);
    }

    [Fact]
    public void Decide_LeModeleNeCiteQueLeDocumentInterdit_RefuseModelCitedForbiddenDocument()
    {
        var fragments = new[]
        {
            Fragment("retards-amendes", AccessLevel.Public),
            Fragment("grille-remuneration", AccessLevel.Confidential),
        };

        var outcome = AnswerPolicy.Decide(
            Sujet, Agent, fragments, Draft("Le montant figure dans la grille.", "grille-remuneration"));

        AssertRefused(outcome, RefusalReason.ModelCitedForbiddenDocument);
    }

    /// <summary>
    /// Contre-epreuve : le meme scenario, mais avec un demandeur habilite. Le refus
    /// disparait. La regle protege l'acces, elle n'interdit pas le document.
    /// </summary>
    [Fact]
    public void Decide_MemeScenarioMaisDemandeurHabilite_RepondNormalement()
    {
        var fragments = new[]
        {
            Fragment("retards-amendes", AccessLevel.Public),
            Fragment("contentieux-usagers", AccessLevel.Confidential),
        };

        var outcome = AnswerPolicy.Decide(
            Sujet, Direction, fragments, Draft("Une réponse complète.", "retards-amendes", "contentieux-usagers"));

        var answered = Assert.IsType<AnswerOutcome.Answered>(outcome);
        Assert.Equal(2, answered.Answer.Citations.Count);
    }

    /// <summary>
    /// Cas mixte : le document cite a un fragment interdit ET un fragment lisible.
    /// Il n'est pas interdit — le demandeur y a bien acces par ailleurs.
    /// </summary>
    [Fact]
    public void Decide_DocumentCiteAvecUnFragmentLisibleParmiDesInterdits_NEstPasConsidereInterdit()
    {
        var fragments = new[]
        {
            Fragment("procedure-accueil", AccessLevel.Internal, ordinal: 0),
            Fragment("procedure-accueil", AccessLevel.Public, ordinal: 1, text: "Le comptoir ouvre à 10 h."),
        };

        var outcome = AnswerPolicy.Decide(
            Sujet, Visiteur, fragments, Draft("Le comptoir ouvre à 10 h.", "procedure-accueil"));

        var answered = Assert.IsType<AnswerOutcome.Answered>(outcome);
        Assert.Equal(1, answered.Answer.Citations[0].ChunkOrdinal);
    }

    // =====================================================================
    // REGLE 8 — chemin nominal
    // =====================================================================

    [Fact]
    public void Decide_ProuveEtCitee_RendUneReponseAvecSesCitations()
    {
        var fragments = new[]
        {
            Fragment("retards-amendes", AccessLevel.Public, text: "Une relance part au bout de sept jours."),
            Fragment("pret-documents", AccessLevel.Public, ordinal: 2, text: "Le prêt court sur vingt-huit jours."),
        };

        var outcome = AnswerPolicy.Decide(
            Sujet,
            Visiteur,
            fragments,
            Draft(
                "Le prêt dure 28 jours [pret-documents] et une relance part à 7 jours [retards-amendes].",
                "pret-documents",
                "retards-amendes"));

        var answered = Assert.IsType<AnswerOutcome.Answered>(outcome);
        Assert.True(outcome.IsAnswered);
        Assert.NotNull(outcome.AnswerOrNull);

        // Les citations suivent l'ordre du modele, pas celui de l'index.
        Assert.Equal(
            new[] { DocumentId.From("pret-documents"), DocumentId.From("retards-amendes") },
            answered.Answer.CitedDocumentIds);

        Assert.Equal("Le prêt court sur vingt-huit jours.", answered.Answer.Citations[0].Excerpt);
        Assert.Equal(2, answered.Answer.Citations[0].ChunkOrdinal);
        Assert.Equal("Titre de pret-documents", answered.Answer.Citations[0].DocumentTitle);
    }

    /// <summary>
    /// Pour un identifiant donne, on retient le PREMIER fragment LISIBLE — pas le
    /// premier fragment tout court. Sinon une citation pourrait exhiber l'extrait
    /// interdit sous couvert d'un document autorise.
    /// </summary>
    [Fact]
    public void Decide_PlusieursFragmentsPourUnMemeDocument_RetientLePremierLisible()
    {
        var fragments = new[]
        {
            Fragment("procedure-accueil", AccessLevel.Internal, ordinal: 0, text: "Consigne réservée aux agents."),
            Fragment("procedure-accueil", AccessLevel.Public, ordinal: 5, text: "Le comptoir ouvre à 10 h."),
            Fragment("procedure-accueil", AccessLevel.Public, ordinal: 9, text: "Le comptoir ferme à 18 h."),
        };

        var outcome = AnswerPolicy.Decide(
            Sujet, Visiteur, fragments, Draft("Le comptoir ouvre à 10 h.", "procedure-accueil"));

        var answered = Assert.IsType<AnswerOutcome.Answered>(outcome);
        var citation = Assert.Single(answered.Answer.Citations);

        Assert.Equal(5, citation.ChunkOrdinal);
        Assert.Equal("Le comptoir ouvre à 10 h.", citation.Excerpt);
    }

    /// <summary>Citer deux fois la meme source n'ajoute pas de source.</summary>
    [Fact]
    public void Decide_MemeIdentifiantCitePlusieursFois_NeProduitQuUneCitation()
    {
        var fragments = new[] { Fragment("retards-amendes", AccessLevel.Public) };

        var outcome = AnswerPolicy.Decide(
            Sujet,
            Visiteur,
            fragments,
            Draft("Retard [retards-amendes], relance [retards-amendes].", "retards-amendes", "retards-amendes"));

        var answered = Assert.IsType<AnswerOutcome.Answered>(outcome);

        Assert.Single(answered.Answer.Citations);
        Assert.Single(answered.Answer.CitedDocumentIds);
    }

    [Fact]
    public void Decide_LongueurDExtraitExplicite_TronqueLesCitations()
    {
        var fragments = new[]
        {
            Fragment("retards-amendes", AccessLevel.Public, text: "Le chat dort sur le tapis rouge."),
        };

        var outcome = AnswerPolicy.Decide(
            Sujet, Visiteur, fragments, Draft("Une réponse.", "retards-amendes"), excerptLength: 10);

        var answered = Assert.IsType<AnswerOutcome.Answered>(outcome);
        Assert.Equal("Le chat…", answered.Answer.Citations[0].Excerpt);
    }

    [Fact]
    public void Decide_TexteDuModeleEntoureDeBlancs_EstTrimeDansLaReponse()
    {
        var fragments = new[] { Fragment("retards-amendes", AccessLevel.Public) };

        var outcome = AnswerPolicy.Decide(
            Sujet, Visiteur, fragments, Draft("   Une réponse.   ", "retards-amendes"));

        var answered = Assert.IsType<AnswerOutcome.Answered>(outcome);
        Assert.Equal("Une réponse.", answered.Answer.Text);
    }

    // =====================================================================
    // Vue d'ensemble
    // =====================================================================

    /// <summary>
    /// Toutes les causes de refus declarees doivent etre atteignables. Une cause qui
    /// ne le serait plus signalerait soit une regle devenue inerte, soit une regle
    /// masquee par une autre placee avant elle — deux facons silencieuses de perdre
    /// une protection.
    /// </summary>
    [Fact]
    public void Decide_ToutesLesCausesDeRefusDeclarees_SontAtteignables()
    {
        var atteintes = new HashSet<RefusalReason>();

        foreach (var outcome in TousLesScenariosDeRefus())
        {
            atteintes.Add(Assert.IsType<AnswerOutcome.Refused>(outcome).Reason);
        }

        foreach (var reason in Enum.GetValues<RefusalReason>())
        {
            Assert.True(
                atteintes.Contains(reason),
                $"Aucun scénario de test ne produit la cause de refus « {reason} » : "
                + "la règle correspondante est soit inerte, soit masquée par une règle antérieure.");
        }
    }

    private static IEnumerable<AnswerOutcome> TousLesScenariosDeRefus()
    {
        var publicFragment = new[] { Fragment("retards-amendes", AccessLevel.Public) };
        var mixte = new[]
        {
            Fragment("retards-amendes", AccessLevel.Public),
            Fragment("contentieux-usagers", AccessLevel.Confidential),
        };
        var interditSeul = new[] { Fragment("contentieux-usagers", AccessLevel.Confidential) };

        yield return AnswerPolicy.Decide(
            Sujet, Visiteur, Array.Empty<EvidenceFragment>(), DraftAnswer.Empty);

        yield return AnswerPolicy.Decide(
            Sujet, Visiteur, interditSeul, Draft("Une réponse.", "contentieux-usagers"));

        yield return AnswerPolicy.Decide(
            Sujet, Visiteur, publicFragment, DraftAnswer.DeclinedByModel);

        yield return AnswerPolicy.Decide(Sujet, Visiteur, publicFragment, DraftAnswer.Empty);

        yield return AnswerPolicy.Decide(
            Sujet, Visiteur, publicFragment, new DraftAnswer("Une réponse.", Array.Empty<DocumentId>()));

        yield return AnswerPolicy.Decide(
            Sujet, Visiteur, publicFragment, Draft("Une réponse.", "document-imaginaire"));

        yield return AnswerPolicy.Decide(
            Sujet, Agent, mixte, Draft("Une réponse.", "contentieux-usagers"));
    }
}
