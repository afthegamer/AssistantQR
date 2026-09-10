using AssistantQR.Application.Model;
using AssistantQR.Domain.Access;
using AssistantQR.Domain.Documents;
using AssistantQR.Domain.Evidence;
using AssistantQR.Infrastructure.LanguageModels;
using AssistantQR.Infrastructure.Prompts;
using AssistantQR.Infrastructure.Tests.Support;

using Xunit;

namespace AssistantQR.Infrastructure.Tests.LanguageModels;

/// <summary>
/// Tests du faux modele extractif.
/// </summary>
/// <remarks>
/// LE BLOC D'EXTRAITS EST CONSTRUIT PAR <c>EvidenceFormatter</c>, JAMAIS A LA MAIN.
/// C'est volontaire : le couplage entre le formateur et ce faux est assume, les deux
/// doivent bouger ensemble. Un test qui recopierait le format attendu en dur
/// continuerait a passer apres un changement du formateur, alors que le systeme reel
/// serait casse — c'est exactement le genre de test qui donne confiance a tort.
///
/// CE QUI EST VERIFIE ICI, C'EST L'OBEISSANCE AU PROMPT. Le gabarit ordonne au modele
/// d'ecrire le marqueur de refus quand les extraits ne permettent pas de repondre ; ce
/// faux simule cette obeissance par un recoupement lexical entre la question et les
/// extraits. Les tests fixent donc les deux bords : il refuse quand rien ne recoupe, il
/// repond quand quelque chose recoupe. La garantie metier, elle, ne depend toujours pas
/// de lui — <c>AnswerPolicy</c> reste le filet qui verifie que les identifiants cites
/// existent et sont lisibles.
///
/// Le dernier test passe par le VRAI gabarit du depot. La question doit etre relue dans
/// le prompt, puisque le port ne transporte qu'une chaine : si un jour le gabarit cesse
/// d'annoncer sa question par un titre de section, c'est ce test-la qui tombera, et pas
/// un instantane trois commandes plus loin.
/// </remarks>
public sealed class ExtractiveLanguageModelTests
{
    private const string HoraireQuestion = "Quels sont les horaires d'ouverture le samedi ?";

    [Fact]
    public async Task CompleteAsync_DeuxExtraitsRecoupentLaQuestion_CiteLesDeux()
    {
        var model = new ExtractiveLanguageModel();
        var prompt = BuildPrompt(
            "Quels sont les horaires d'ouverture et les regles de pret des documents ?",
            HoraireFragment(),
            PretFragment());

        var completion = await model.CompleteAsync(Request(prompt));

        Assert.Contains("[horaires-ouverture]", completion.Text, StringComparison.Ordinal);
        Assert.Contains("[pret-documents]", completion.Text, StringComparison.Ordinal);
        Assert.StartsWith("D'après les documents consultés :", completion.Text, StringComparison.Ordinal);
        Assert.Equal("extractive-fake", completion.ModelId);
    }

    [Fact]
    public async Task CompleteAsync_ExtraitPertinent_RecopieLeTexteSansRienInventer()
    {
        var model = new ExtractiveLanguageModel();
        var prompt = BuildPrompt(HoraireQuestion, HoraireFragment());

        var completion = await model.CompleteAsync(Request(prompt));

        // Il n'engendre rien, il extrait : la premiere phrase du fragment doit se
        // retrouver telle quelle dans la reponse.
        Assert.Contains(
            "La médiathèque ouvre du mardi au samedi de dix heures à dix-neuf heures.",
            completion.Text,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompleteAsync_UnSeulExtraitRecoupeLaQuestion_NeCiteQueCeluiLa()
    {
        var model = new ExtractiveLanguageModel();

        // « horaires » et « ouverture » ne figurent que dans le premier bloc ; le second
        // parle de pret et d'abonnes, et passe le seuil de score sans parler du sujet.
        var prompt = BuildPrompt(HoraireQuestion, HoraireFragment(), PretFragment());

        var completion = await model.CompleteAsync(Request(prompt));

        Assert.Contains("[horaires-ouverture]", completion.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("[pret-documents]", completion.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompleteAsync_TroisExtraitsPertinents_NEnCiteQueDeux()
    {
        var model = new ExtractiveLanguageModel();
        var prompt = BuildPrompt(
            "Quelles regles de pret pour les documents des enfants ?",
            PretFragment(),
            JeunesseFragment(),
            AutrePretFragment());

        var completion = await model.CompleteAsync(Request(prompt));

        // Deux citations au maximum, et ce sont les deux premieres du prompt — donc les
        // mieux classees par la recuperation.
        Assert.Contains("[pret-documents]", completion.Text, StringComparison.Ordinal);
        Assert.Contains("[espace-jeunesse]", completion.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("[catalogue-en-ligne]", completion.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompleteAsync_AucunExtraitNeRecoupeLaQuestion_RenvoieLeMarqueurDeRefus()
    {
        var model = new ExtractiveLanguageModel();

        // Question hors corpus : les extraits ont passe le seuil de score sans avoir le
        // moindre rapport avec elle. La regle metier annonce un refus, le gabarit
        // l'ordonne, le faux doit l'emettre.
        var prompt = BuildPrompt(
            "Est-ce que la mediatheque prete des velos aux abonnes ?",
            HoraireFragment(),
            JeunesseFragment());

        var completion = await model.CompleteAsync(Request(prompt));

        Assert.Equal(ModelResponseParser.RefusalMarker, completion.Text);
    }

    [Fact]
    public async Task CompleteAsync_QuestionSansAccents_RecoupeQuandMemeLesExtraitsAccentues()
    {
        var model = new ExtractiveLanguageModel();
        var prompt = BuildPrompt("Que propose l'espace jeunesse aux enfants ?", JeunesseFragment());

        var completion = await model.CompleteAsync(Request(prompt));

        // « jeunesse » ecrit sans accent doit recouper « L'espace jeunesse » : la
        // normalisation s'applique des deux cotes de la comparaison.
        Assert.Contains("[espace-jeunesse]", completion.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompleteAsync_AucunBlocDExtraits_RenvoieLeMarqueurDeRefus()
    {
        var model = new ExtractiveLanguageModel();

        var completion = await model.CompleteAsync(Request(
            "Tu es un assistant documentaire.\n\nQuestion : quels sont les horaires ?\n\nRéponds en français."));

        // On emet le marqueur de refus plutot que d'inventer. Le parseur d'Application
        // le traduira en brouillon vide, et la politique metier en refus argumente.
        Assert.Equal(ModelResponseParser.RefusalMarker, completion.Text);
    }

    [Fact]
    public async Task CompleteAsync_BlocDExtraitsVide_RenvoieLeMarqueurDeRefus()
    {
        var model = new ExtractiveLanguageModel();
        var prompt = BuildPrompt(HoraireQuestion);

        var completion = await model.CompleteAsync(Request(prompt));

        Assert.Equal(ModelResponseParser.RefusalMarker, completion.Text);
    }

    [Fact]
    public async Task CompleteAsync_QuestionIntrouvableDansLePrompt_CiteLesDeuxPremiersExtraits()
    {
        var model = new ExtractiveLanguageModel();

        // Prompt sans section « Question » : le critere de pertinence n'a pas de matiere.
        // On revient alors au comportement d'origine plutot que de tout refuser — une
        // doublure qui refuserait tout en bloc serait une panne silencieuse et totale,
        // imputee au corpus alors qu'elle viendrait du gabarit.
        var prompt = string.Join("\n\n",
            "Tu es l'assistant documentaire de la médiathèque municipale des Tilleuls.",
            EvidenceFormatter.Format(Scored(HoraireFragment(), PretFragment())),
            "Réponds en français en citant tes sources.");

        var completion = await model.CompleteAsync(Request(prompt));

        Assert.Contains("[horaires-ouverture]", completion.Text, StringComparison.Ordinal);
        Assert.Contains("[pret-documents]", completion.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompleteAsync_MemeRequete_RendDeuxFoisLaMemeReponse()
    {
        var model = new ExtractiveLanguageModel();
        var prompt = BuildPrompt(HoraireQuestion, HoraireFragment(), PretFragment());

        var first = await model.CompleteAsync(Request(prompt));
        var second = await model.CompleteAsync(Request(prompt));

        Assert.Equal(first.Text, second.Text);
    }

    [Fact]
    public async Task CompleteAsync_DeuxInstances_RendentLaMemeReponse()
    {
        var prompt = BuildPrompt(HoraireQuestion, HoraireFragment(), PretFragment());

        var first = await new ExtractiveLanguageModel().CompleteAsync(Request(prompt));
        var second = await new ExtractiveLanguageModel().CompleteAsync(Request(prompt));

        // Aucun etat, aucune horloge, aucun aleatoire : deux instances distinctes
        // rendent octet pour octet la meme chose.
        Assert.Equal(first.Text, second.Text);
    }

    [Theory]
    [InlineData(0.0, 42)]
    [InlineData(1.5, null)]
    public async Task CompleteAsync_TemperatureEtGraine_SontIgnoreesDeliberement(double temperature, int? seed)
    {
        var model = new ExtractiveLanguageModel();
        var prompt = BuildPrompt(HoraireQuestion, HoraireFragment());

        var reference = await model.CompleteAsync(Request(prompt));
        var variant = await model.CompleteAsync(new LlmRequest(prompt, temperature, seed, MaxTokens: 600));

        // La meme requete rend octet pour octet la meme reponse, aujourd'hui et dans six
        // mois. Un test qui echoue accuse donc le code, jamais le modele : c'est la
        // seule facon de faire d'un instantane une ligne de base stable.
        Assert.Equal(reference.Text, variant.Text);
    }

    [Fact]
    public async Task CompleteAsync_PromptEnFinsDeLigneWindows_LitQuandMemeLesExtraits()
    {
        var model = new ExtractiveLanguageModel();
        var prompt = BuildPrompt(HoraireQuestion, HoraireFragment())
            .Replace("\n", "\r\n", StringComparison.Ordinal);

        var completion = await model.CompleteAsync(Request(prompt));

        // Le gabarit vient d'un fichier qui peut etre en CRLF alors que le bloc
        // d'extraits est toujours en LF : le faux doit survivre au melange.
        Assert.Contains("[horaires-ouverture]", completion.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompleteAsync_RequeteNulle_EstRefusee()
    {
        var model = new ExtractiveLanguageModel();

        await Assert.ThrowsAsync<ArgumentNullException>(() => model.CompleteAsync(null!));
    }

    [Fact]
    public async Task CompleteAsync_VraiGabaritDuDepot_RepondQuandLExtraitRecoupeEtRefuseSinon()
    {
        var model = new ExtractiveLanguageModel();
        var template = new FileSystemPromptCatalog(RepositoryLayout.PromptsDirectory)
            .Get("answer-with-citations", "1.0.0");

        var pertinent = await model.CompleteAsync(Request(Render(template, HoraireQuestion, HoraireFragment())));
        var horsSujet = await model.CompleteAsync(Request(Render(
            template,
            "Est-ce que la mediatheque prete des velos aux abonnes ?",
            HoraireFragment())));

        // Le prompt reel annonce sa question par « ## Question posée ». C'est le seul
        // repere dont dispose ce faux pour la relire : si le gabarit change de forme,
        // ce test tombe, et c'est exactement ce qu'on lui demande.
        Assert.Contains("[horaires-ouverture]", pertinent.Text, StringComparison.Ordinal);
        Assert.Equal(ModelResponseParser.RefusalMarker, horsSujet.Text);
    }

    /// <summary>
    /// UN MOT DANS LE TITRE, UN MOT DANS LE TEXTE, AUCUN RAPPORT AVEC LA QUESTION.
    /// « Quelle est la remuneration d'un agent d'accueil ? » porte trois mots
    /// significatifs : remuneration, agent, accueil. La procedure d'accueil en recoupe
    /// deux — « accueil » vient de son titre, « agent » de son texte — et ne dit pas un
    /// mot des salaires : le seul document qui reponde est confidentiel, et le demandeur
    /// n'y a pas droit. Le faux doit refuser plutot que citer proprement un document a
    /// cote de la question.
    /// </summary>
    /// <remarks>
    /// C'EST CE CAS QUI IMPOSE DE COMPTER LES DEUX ENDROITS SEPAREMENT. Tant que les
    /// recoupements de l'en-tete et ceux du texte etaient additionnes, un mot pris ici et
    /// un mot pris la atteignaient le seuil, et la ligne de commande rendait une reponse
    /// sourcee, plausible, et hors sujet a une question dont la vraie reponse etait
    /// interdite au demandeur. Le pire des resultats : ni la bonne reponse, ni un refus.
    /// </remarks>
    [Fact]
    public async Task CompleteAsync_UnMotDansLEnteteUnMotDansLeTexte_NeSuffitPas()
    {
        var model = new ExtractiveLanguageModel();
        var prompt = BuildPrompt(
            "Quelle est la remuneration d'un agent d'accueil ?",
            AccueilFragment());

        var completion = await model.CompleteAsync(Request(prompt));

        Assert.Equal(ModelResponseParser.RefusalMarker, completion.Text);
    }

    /// <summary>
    /// LE PENDANT DU TEST PRECEDENT : deux mots AU MEME ENDROIT suffisent. Ici les deux
    /// viennent de l'en-tete, et le texte du morceau n'en contient aucun — c'est le cas
    /// courant apres decoupage, ou le paragraphe retenu parle de la mise en oeuvre pendant
    /// que le titre porte le sujet. Refuser celui-la rendrait muet un systeme qui tient
    /// pourtant la bonne source.
    /// </summary>
    [Fact]
    public async Task CompleteAsync_DeuxMotsDansLaSeuleEntete_SuffisentACiter()
    {
        var model = new ExtractiveLanguageModel();
        var prompt = BuildPrompt(
            "Comment sont traites les retards en interne ?",
            RetardsInterneFragment());

        var completion = await model.CompleteAsync(Request(prompt));

        Assert.Contains("[gestion-retards-interne]", completion.Text, StringComparison.Ordinal);
    }

    private static LlmRequest Request(string prompt) => new(prompt, Temperature: 0.0, Seed: 42, MaxTokens: 600);

    /// <summary>Rend le gabarit reel exactement comme le fait le cas d'usage.</summary>
    private static string Render(PromptTemplate template, string question, params EvidenceFragment[] fragments) =>
        template.Render(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["question"] = question,
            ["evidence"] = EvidenceFormatter.Format(Scored(fragments)),
            ["refusal_marker"] = ModelResponseParser.RefusalMarker,
        });

    /// <summary>
    /// Reconstruit un prompt complet, bloc d'extraits inclus, exactement comme le fait
    /// le cas d'usage : en passant par <see cref="EvidenceFormatter"/>.
    /// </summary>
    private static string BuildPrompt(string question, params EvidenceFragment[] fragments) =>
        string.Join("\n\n",
            "Tu es l'assistant documentaire de la médiathèque municipale des Tilleuls.",
            "## Extraits disponibles",
            EvidenceFormatter.Format(Scored(fragments)),
            "## Question posée",
            question,
            "Réponds en français en citant tes sources.");

    private static IReadOnlyList<ScoredFragment> Scored(params EvidenceFragment[] fragments) =>
        fragments.Select((fragment, rank) => new ScoredFragment(fragment, 1.0 - (rank * 0.1))).ToList();

    private static EvidenceFragment HoraireFragment() => new(
        DocumentId.From("horaires-ouverture"),
        "Horaires d'ouverture au public",
        "La médiathèque ouvre du mardi au samedi de dix heures à dix-neuf heures. Le dimanche, elle reste fermée.",
        AccessLevel.Public,
        0);

    private static EvidenceFragment PretFragment() => new(
        DocumentId.From("pret-documents"),
        "Règles de prêt et de retour",
        "Chaque abonné peut emprunter jusqu'à dix documents pour trois semaines. Une prolongation reste possible.",
        AccessLevel.Public,
        1);

    /// <summary>Titre porteur d'« accueil », texte porteur d'« agent », rien sur les salaires.</summary>
    private static EvidenceFragment AccueilFragment() => new(
        DocumentId.From("procedure-accueil"),
        "Procédure d'accueil au comptoir",
        "L'agent seul au comptoir dispose d'un renfort joignable par le téléphone interne.",
        AccessLevel.Internal,
        1);

    /// <summary>Titre porteur de « retards » et « interne » ; le texte, lui, n'en dit rien.</summary>
    private static EvidenceFragment RetardsInterneFragment() => new(
        DocumentId.From("gestion-retards-interne"),
        "Traitement interne des retards",
        "Un agent peut lever une suspension du droit de prêt une fois par usager et par année civile.",
        AccessLevel.Internal,
        4);

    private static EvidenceFragment JeunesseFragment() => new(
        DocumentId.From("espace-jeunesse"),
        "L'espace jeunesse",
        "L'espace jeunesse accueille les enfants jusqu'à quatorze ans dans une salle dédiée. Le prêt y est gratuit.",
        AccessLevel.Public,
        0);

    private static EvidenceFragment AutrePretFragment() => new(
        DocumentId.From("catalogue-en-ligne"),
        "Catalogue en ligne et compte lecteur",
        "Le catalogue affiche les documents empruntables et l'état de chaque prêt en cours. La prolongation se demande en ligne.",
        AccessLevel.Public,
        2);
}
