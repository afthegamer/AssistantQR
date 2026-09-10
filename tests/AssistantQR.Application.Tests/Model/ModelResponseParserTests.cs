using AssistantQR.Application.Model;

using Xunit;

namespace AssistantQR.Application.Tests.Model;

/// <summary>
/// Le parseur ne VALIDE rien : il traduit du texte libre en donnees exploitables. Le
/// jugement appartient a <c>AnswerPolicy</c>, dans le Domain. Cette separation est ce
/// qui permet de rester tolerant ici — un identifiant mal forme est du bruit de
/// generation, pas une panne — sans jamais relacher la severite la ou elle compte.
/// </summary>
public sealed class ModelResponseParserTests
{
    private static IReadOnlyList<string> IdsOf(string raw)
    {
        var draft = ModelResponseParser.Parse(raw);
        var ids = new List<string>(draft.CitedDocumentIds.Count);
        foreach (var id in draft.CitedDocumentIds)
        {
            ids.Add(id.Value);
        }

        return ids;
    }

    [Fact]
    public void Parse_SingleCitation_ExtractsTheIdentifier()
    {
        var draft = ModelResponseParser.Parse("La mediatheque ouvre a 10 h [horaires-ouverture].");

        Assert.Equal(new[] { "horaires-ouverture" }, IdsOf("La mediatheque ouvre a 10 h [horaires-ouverture]."));
        Assert.Equal("La mediatheque ouvre a 10 h [horaires-ouverture].", draft.Text);
    }

    [Fact]
    public void Parse_SeveralCitations_PreservesOrderOfAppearance()
    {
        // L'ordre n'est pas cosmetique : AnswerPolicy construit les citations dans cet
        // ordre, donc c'est lui qui determine la mise en forme de la reponse finale.
        Assert.Equal(
            new[] { "pret-documents", "retards-amendes", "accessibilite" },
            IdsOf("D'abord [pret-documents], ensuite [retards-amendes], enfin [accessibilite]."));
    }

    [Fact]
    public void Parse_RepeatedCitation_IsDeduplicated()
    {
        // Citer deux fois la meme source n'ajoute pas de source. Le doublon serait de
        // surcroit rejete par Answer.Create : autant l'ecarter ici.
        Assert.Equal(
            new[] { "horaires-ouverture", "accessibilite" },
            IdsOf("[horaires-ouverture] puis [accessibilite] puis encore [horaires-ouverture]."));
    }

    [Fact]
    public void Parse_RefusalMarkerAlone_ReturnsEmptyDraft()
    {
        var draft = ModelResponseParser.Parse(ModelResponseParser.RefusalMarker);

        Assert.Equal(string.Empty, draft.Text);
        Assert.Empty(draft.CitedDocumentIds);
    }

    [Fact]
    public void Parse_RefusalMarkerWithSurroundingWhitespace_ReturnsEmptyDraft()
    {
        var draft = ModelResponseParser.Parse("\n  AUCUNE_REPONSE \n");

        Assert.Equal(string.Empty, draft.Text);
        Assert.Empty(draft.CitedDocumentIds);
    }

    [Fact]
    public void Parse_RefusalMarkerInLowerCase_IsStillARefusal()
    {
        Assert.Empty(ModelResponseParser.Parse("aucune_reponse").CitedDocumentIds);
        Assert.Equal(string.Empty, ModelResponseParser.Parse("aucune_reponse").Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t ")]
    public void Parse_EmptyResponse_ReturnsEmptyDraft(string raw)
    {
        var draft = ModelResponseParser.Parse(raw);

        Assert.Equal(string.Empty, draft.Text);
        Assert.Empty(draft.CitedDocumentIds);
    }

    [Theory]
    [InlineData("Voir [document avec espace].")]
    [InlineData("Voir [-tiret-en-tete].")]
    [InlineData("Voir [_underscore].")]
    [InlineData("Voir [].")]
    [InlineData("Voir [accents-é].")]
    public void Parse_MalformedCitationMarker_IsSilentlyIgnored(string raw)
    {
        // Aucun refus, aucune exception : le texte est conserve tel quel et se retrouvera
        // sans citation. C'est AnswerPolicy qui prononcera le refus « aucune source »,
        // au bon endroit et avec la bonne explication.
        var draft = ModelResponseParser.Parse(raw);

        Assert.Empty(draft.CitedDocumentIds);
        Assert.Equal(raw, draft.Text);
    }

    [Fact]
    public void Parse_MixOfValidAndMalformedMarkers_KeepsOnlyTheValidOnes()
    {
        Assert.Equal(
            new[] { "horaires-ouverture" },
            IdsOf("Voir [horaires-ouverture] et [pas valide] et []."));
    }

    [Fact]
    public void Parse_TextIsReturnedVerbatim_MarkersIncluded()
    {
        // Le parseur ne nettoie pas le texte : les marqueurs restent visibles dans la
        // reponse affichee a l'usager, ce qui est precisement l'interet d'une citation
        // en ligne — la source se lit a l'endroit ou l'affirmation est faite.
        const string raw = "Le pret dure 21 jours [pret-documents].";

        Assert.Equal(raw, ModelResponseParser.Parse(raw).Text);
    }

    [Fact]
    public void RefusalMarker_IsTheValueImposedByThePromptTemplates()
    {
        Assert.Equal("AUCUNE_REPONSE", ModelResponseParser.RefusalMarker);
    }

    // =====================================================================
    // Deux silences qui ne se ressemblent pas
    // =====================================================================

    /// <summary>
    /// Le marqueur seul est une DECLARATION du modele, pas une absence de sortie. Les
    /// deux propositions portent un texte vide ; seul le drapeau permet au Domain de
    /// distinguer l'obeissance a la regle metier de la panne technique.
    /// </summary>
    [Theory]
    [InlineData("AUCUNE_REPONSE")]
    [InlineData("  AUCUNE_REPONSE  ")]
    [InlineData("\n\t AUCUNE_REPONSE \r\n")]
    [InlineData("aucune_reponse")]
    [InlineData("AUCUNE_REPONSE [horaires-ouverture]")]
    public void Parse_RefusalMarker_ProducesADeclinedDraft(string raw)
    {
        var draft = ModelResponseParser.Parse(raw);

        Assert.True(draft.Declined);
        Assert.Equal(string.Empty, draft.Text);
        Assert.Empty(draft.CitedDocumentIds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t ")]
    public void Parse_EmptyResponse_ProducesANonDeclinedDraft(string raw)
    {
        var draft = ModelResponseParser.Parse(raw);

        Assert.False(draft.Declined);
        Assert.Equal(string.Empty, draft.Text);
    }

    /// <summary>
    /// Un modele bavard qui ponctue son refus (« AUCUNE_REPONSE. ») ne declare pas :
    /// le marqueur impose est exact, et ce texte-la suivra le chemin ordinaire. Le refus
    /// viendra alors de l'absence de citation, au bon endroit et avec la bonne cause.
    /// </summary>
    [Theory]
    [InlineData("AUCUNE_REPONSE.")]
    [InlineData("Desole : AUCUNE_REPONSE")]
    public void Parse_RefusalMarkerWithExtraPunctuationOrWords_IsNotADeclaration(string raw)
    {
        var draft = ModelResponseParser.Parse(raw);

        Assert.False(draft.Declined);
        Assert.Equal(raw, draft.Text);
    }

    [Fact]
    public void Parse_OrdinaryAnswer_IsNotADeclaration()
    {
        var draft = ModelResponseParser.Parse("Le pret dure 21 jours [pret-documents].");

        Assert.False(draft.Declined);
    }
}
