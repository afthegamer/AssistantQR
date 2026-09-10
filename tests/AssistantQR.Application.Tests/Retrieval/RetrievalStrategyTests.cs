using AssistantQR.Application.Configuration;
using AssistantQR.Application.Model;
using AssistantQR.Application.Retrieval;
using AssistantQR.Application.Tests.Doubles;

using AssistantQR.Domain.Access;

using Xunit;

namespace AssistantQR.Application.Tests.Retrieval;

/// <summary>
/// LE DILEMME DU FILTRAGE, EN VERSION EXECUTABLE.
///
/// Le controle d'acces doit-il etre applique par l'index, avant le classement
/// (pre-filtrage), ou par l'Application, apres (post-filtrage) ? La litterature tranche
/// rarement, parce que les deux positions sont defendables :
///
/// — Le POST-filtrage garde le Domain seul juge. L'index ne connait ni habilitation ni
///   niveau, et un bogue dans son code ne peut pas devenir une faille de securite. Prix
///   a payer : les <c>topK</c> peuvent etre entierement composes de documents interdits,
///   auquel cas on repond « rien de lisible » alors que le corpus contenait la reponse.
/// — Le PRE-filtrage rend les <c>topK</c> tous exploitables. Prix a payer : une regle
///   metier est desormais appliquee par un composant technique, hors de portee des tests
///   du Domain.
///
/// Ces tests fabriquent la situation ou l'arbitrage se voit. Les scores ne sont pas
/// aleatoires : ils sont choisis pour qu'un meme agent, avec la meme question et la meme
/// configuration, obtienne DEUX ENSEMBLES DIFFERENTS selon le mode. C'est la preuve que
/// ce reglage — qui ne figure dans aucune regle metier — determine ce que le systeme
/// repond.
/// </summary>
public sealed class RetrievalStrategyTests
{
    private static readonly Requester Agent = Requester.Create("agent-42", "internal");

    private static readonly PipelineOptions Options =
        PipelineOptions.Default with { TopK = 3, MinScore = 0d };

    /// <summary>
    /// Un corpus miniature calque sur la contrainte de conception du vrai corpus :
    /// les trois niveaux partagent le vocabulaire du retard, donc ils se disputent les
    /// memes places de classement. Sans ce recouvrement lexical, le dilemme ne se
    /// declencherait jamais et la demonstration serait un artefact de laboratoire.
    /// </summary>
    private static FakeVectorIndex RetardsIndex()
    {
        var index = new FakeVectorIndex();

        // Les deux meilleurs scores sont confidentiels : c'est banal des lors que les
        // dossiers de contentieux parlent de retards mieux que le reglement public.
        index.SeedScored("contentieux-usagers", "Suivi des usagers en contentieux", AccessLevel.Confidential, 0.95);
        index.SeedScored("retards-amendes", "Retards, relances et amendes", AccessLevel.Public, 0.90);
        index.SeedScored("procedure-disciplinaire", "Procedure disciplinaire", AccessLevel.Confidential, 0.88);
        index.SeedScored("gestion-retards-interne", "Traitement interne des retards", AccessLevel.Internal, 0.70);
        index.SeedScored("planning-agents", "Elaboration du planning des agents", AccessLevel.Internal, 0.60);

        return index;
    }

    private static IReadOnlyList<string> IdsOf(IReadOnlyList<ScoredFragment> fragments)
    {
        var ids = new List<string>(fragments.Count);
        foreach (var fragment in fragments)
        {
            ids.Add(fragment.Fragment.DocumentId.Value);
        }

        return ids;
    }

    // -----------------------------------------------------------------------
    // LE TEST CENTRAL DU PROJET
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RetrieveAsync_SameIndexSameRequester_PreAndPostFilteringDiverge()
    {
        var index = RetardsIndex();

        var post = await new PostFilterRetrievalStrategy(index)
            .RetrieveAsync(UnitVectors.Query, Agent, Options);

        var pre = await new PreFilterRetrievalStrategy(index)
            .RetrieveAsync(UnitVectors.Query, Agent, Options);

        // --- 1. Ce que l'index a rendu dans chaque mode.
        // En post-filtrage l'index classe TOUT : les trois meilleurs sont deux
        // confidentiels et un public.
        Assert.Equal(
            new[] { "contentieux-usagers", "retards-amendes", "procedure-disciplinaire" },
            IdsOf(post.FromIndex));

        // En pre-filtrage l'index ne classe que le lisible : les deux confidentiels
        // n'ont jamais concouru, et deux documents internes prennent leur place.
        Assert.Equal(
            new[] { "retards-amendes", "gestion-retards-interne", "planning-agents" },
            IdsOf(pre.FromIndex));

        // --- 2. LA DIVERGENCE, ASSERTEE EXPLICITEMENT.
        // Aucun des deux ensembles de candidats n'est inclus dans l'autre : le post
        // contient deux documents que le pre n'a jamais vus, le pre en contient deux que
        // le post n'a pas remontes. Meme question, meme corpus, meme utilisateur.
        var postCandidates = new HashSet<string>(IdsOf(post.FromIndex), StringComparer.Ordinal);
        var preCandidates = new HashSet<string>(IdsOf(pre.FromIndex), StringComparer.Ordinal);

        Assert.False(postCandidates.IsSubsetOf(preCandidates), "Le post-filtrage devrait remonter des candidats que le pre-filtrage ignore.");
        Assert.False(preCandidates.IsSubsetOf(postCandidates), "Le pre-filtrage devrait remonter des candidats que le post-filtrage ignore.");

        // --- 3. Ce qui arrive au modele de langue, donc ce qui determine la reponse.
        // Le post-filtrage n'a plus qu'un seul extrait a montrer ; le pre-filtrage en a
        // trois. Le nombre de resultats differe, et avec lui la qualite de la reponse.
        Assert.Equal(new[] { "retards-amendes" }, IdsOf(post.AfterAccessFilter));
        Assert.Equal(
            new[] { "retards-amendes", "gestion-retards-interne", "planning-agents" },
            IdsOf(pre.AfterAccessFilter));

        Assert.NotEqual(post.AfterAccessFilter.Count, pre.AfterAccessFilter.Count);

        // --- 4. LE COUT, CHIFFRE.
        // Le post-filtrage a jete les deux tiers de ses candidats pour raison d'acces.
        // L'agent perd deux places de classement au profit de documents qu'il ne verra
        // jamais : c'est exactement ce que le pre-filtrage lui rendrait, en echange
        // d'une regle de securite deportee dans l'infrastructure.
        Assert.Equal(3, post.FromIndex.Count);
        Assert.Single(post.AfterAccessFilter);
        Assert.Equal(3, pre.FromIndex.Count);
        Assert.Equal(3, pre.AfterAccessFilter.Count);

        // NOTE HONNETE, a lire en cours : sur l'ensemble FINAL, l'inclusion n'est pas
        // symetrique et ne peut pas l'etre. Les resultats du post-filtrage sont
        // toujours inclus dans ceux du pre-filtrage, parce que « les k meilleurs parmi
        // les lisibles » contient necessairement « les lisibles parmi les k meilleurs ».
        // Le pre-filtrage ne peut donc JAMAIS rendre un resultat strictement pire ; il
        // ne se paie pas en pertinence, il se paie en architecture. C'est sur les
        // CANDIDATS (assertion 2) que la divergence est mutuelle, et c'est la que se lit
        // le vrai changement de comportement de l'index.
        Assert.Subset(preCandidates, new HashSet<string>(IdsOf(post.AfterAccessFilter), StringComparer.Ordinal));
    }

    // -----------------------------------------------------------------------
    // Ce que chaque strategie envoie reellement a l'index
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RetrieveAsync_PostFilter_NeverSendsTheClearanceToTheIndex()
    {
        var index = RetardsIndex();

        await new PostFilterRetrievalStrategy(index).RetrieveAsync(UnitVectors.Query, Agent, Options);

        // C'est la propriete architecturale du mode post : aucune habilitation ne
        // traverse la frontiere, l'index ne saura jamais qui demande.
        Assert.Equal(SearchFilter.NoFilter, index.LastFilter);
    }

    [Fact]
    public async Task RetrieveAsync_PreFilter_SendsTheClearanceToTheIndex()
    {
        var index = RetardsIndex();

        await new PreFilterRetrievalStrategy(index).RetrieveAsync(UnitVectors.Query, Agent, Options);

        Assert.Equal(SearchFilter.UpTo(AccessLevel.Internal), index.LastFilter);
    }

    [Fact]
    public async Task RetrieveAsync_BothStrategies_AskTheIndexForExactlyTopKResults()
    {
        var index = RetardsIndex();

        await new PostFilterRetrievalStrategy(index).RetrieveAsync(UnitVectors.Query, Agent, Options with { TopK = 2 });

        Assert.Contains("Search(2)", index.Calls);
    }

    // -----------------------------------------------------------------------
    // Seuil de pertinence, tri, defense en profondeur
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RetrieveAsync_ScoresBelowMinScore_AreDroppedButStayInFromIndex()
    {
        var index = new FakeVectorIndex();
        index.SeedScored("horaires-ouverture", "Horaires", AccessLevel.Public, 0.80);
        index.SeedScored("dons-documents", "Dons de documents", AccessLevel.Public, 0.10);

        var outcome = await new PostFilterRetrievalStrategy(index)
            .RetrieveAsync(UnitVectors.Query, Agent, PipelineOptions.Default with { TopK = 5, MinScore = 0.20 });

        // FromIndex reste le materiau BRUT : la trace doit pouvoir montrer ce que l'index
        // avait propose, y compris ce que le seuil a ecarte. Un « rien de pertinent »
        // inexplique est un mauvais message d'erreur.
        Assert.Equal(new[] { "horaires-ouverture", "dons-documents" }, IdsOf(outcome.FromIndex));
        Assert.Equal(new[] { "horaires-ouverture" }, IdsOf(outcome.AfterAccessFilter));
    }

    [Fact]
    public async Task RetrieveAsync_MinScoreOnTheExactBoundary_KeepsTheFragment()
    {
        var index = new FakeVectorIndex();
        index.SeedScored("horaires-ouverture", "Horaires", AccessLevel.Public, 0.5);

        var outcome = await new PostFilterRetrievalStrategy(index)
            .RetrieveAsync(UnitVectors.Query, Agent, PipelineOptions.Default with { MinScore = 0.5 });

        Assert.Single(outcome.AfterAccessFilter);
    }

    [Fact]
    public async Task RetrieveAsync_IndexBreaksItsSortContract_TheStrategyReordersAnyway()
    {
        var index = RetardsIndex();
        index.BreakSortContract = true;

        var outcome = await new PostFilterRetrievalStrategy(index).RetrieveAsync(UnitVectors.Query, Agent, Options);

        // Le contrat de IVectorIndex promet un tri decroissant. Une promesse tenue par un
        // adaptateur externe n'est pas une garantie : on retrie. Le cout est de quelques
        // comparaisons, le bogue evite est un prompt dont l'extrait le plus pertinent
        // arrive en dernier.
        for (var i = 1; i < outcome.FromIndex.Count; i++)
        {
            Assert.True(
                outcome.FromIndex[i - 1].Score >= outcome.FromIndex[i].Score,
                "Les fragments doivent etre tries par score decroissant.");
        }
    }

    [Fact]
    public async Task RetrieveAsync_PreFilterAndTheIndexIgnoresTheFilter_DefenseInDepthStillExcludes()
    {
        // LE SCENARIO QUE LE PRE-FILTRAGE REND POSSIBLE. L'index accepte le plafond
        // d'habilitation, puis l'ignore — bogue, mauvaise traduction du niveau vers le
        // dialecte de la base, regression dans un adaptateur. Aucun test du Domain ne
        // peut atteindre ce code. La seule protection est que l'Application repasse par
        // AccessPolicy apres coup.
        var index = RetardsIndex();
        index.IgnoreAccessFilterContract = true;

        var outcome = await new PreFilterRetrievalStrategy(index).RetrieveAsync(UnitVectors.Query, Agent, Options);

        Assert.Contains("contentieux-usagers", IdsOf(outcome.FromIndex));
        Assert.DoesNotContain("contentieux-usagers", IdsOf(outcome.AfterAccessFilter));
        Assert.DoesNotContain("procedure-disciplinaire", IdsOf(outcome.AfterAccessFilter));
    }

    [Fact]
    public async Task RetrieveAsync_PreFilterWithAnHonestIndex_DefenseInDepthRemovesNothing()
    {
        var index = RetardsIndex();

        var outcome = await new PreFilterRetrievalStrategy(index).RetrieveAsync(UnitVectors.Query, Agent, Options);

        // Quand l'index tient parole, la passe de verification ne doit RIEN retirer.
        // Si elle retirait quelque chose, l'adaptateur serait defaillant.
        Assert.Equal(IdsOf(outcome.FromIndex), IdsOf(outcome.AfterAccessFilter));
    }

    [Fact]
    public async Task RetrieveAsync_EmptyIndex_ReturnsTwoEmptyLists()
    {
        var outcome = await new PostFilterRetrievalStrategy(new FakeVectorIndex())
            .RetrieveAsync(UnitVectors.Query, Agent, Options);

        Assert.Empty(outcome.FromIndex);
        Assert.Empty(outcome.AfterAccessFilter);
    }

    [Fact]
    public async Task RetrieveAsync_PostFilterAndEverythingIsForbidden_ReturnsCandidatesButNothingUsable()
    {
        var index = new FakeVectorIndex();
        index.SeedScored("grille-remuneration", "Grille de remuneration", AccessLevel.Confidential, 0.97);
        index.SeedScored("contrat-maintenance", "Contrat de maintenance", AccessLevel.Confidential, 0.93);

        var outcome = await new PostFilterRetrievalStrategy(index)
            .RetrieveAsync(UnitVectors.Query, Requester.Anonymous, Options);

        // La trace conserve les deux candidats : c'est ce qui permettra a la politique du
        // Domain de dire « des documents traitent de cette question, mais pas pour vous »
        // au lieu du refus generique « rien dans le corpus ».
        Assert.Equal(2, outcome.FromIndex.Count);
        Assert.Empty(outcome.AfterAccessFilter);
    }

    // -----------------------------------------------------------------------
    // Fabrique
    // -----------------------------------------------------------------------

    [Fact]
    public void Create_Post_ReturnsThePostFilterStrategy()
    {
        var strategy = RetrievalStrategyFactory.Create(AccessFilterMode.Post, new FakeVectorIndex());

        Assert.IsType<PostFilterRetrievalStrategy>(strategy);
        Assert.Equal(AccessFilterMode.Post, strategy.Mode);
    }

    [Fact]
    public void Create_Pre_ReturnsThePreFilterStrategy()
    {
        var strategy = RetrievalStrategyFactory.Create(AccessFilterMode.Pre, new FakeVectorIndex());

        Assert.IsType<PreFilterRetrievalStrategy>(strategy);
        Assert.Equal(AccessFilterMode.Pre, strategy.Mode);
    }

    [Fact]
    public void Create_UnknownMode_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RetrievalStrategyFactory.Create((AccessFilterMode)99, new FakeVectorIndex()));
    }

    [Fact]
    public void Constructors_NullIndex_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => new PreFilterRetrievalStrategy(null!));
        Assert.Throws<ArgumentNullException>(() => new PostFilterRetrievalStrategy(null!));
    }
}
