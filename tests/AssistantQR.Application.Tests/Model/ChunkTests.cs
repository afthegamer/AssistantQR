using AssistantQR.Application.Model;

using AssistantQR.Domain.Access;
using AssistantQR.Domain.Documents;

using Xunit;

namespace AssistantQR.Application.Tests.Model;

/// <summary>
/// Le morceau est le point de passage entre la mecanique de recherche et le vocabulaire
/// metier. Ces tests verifient surtout ce qu'il PERD en traversant la frontiere :
/// le Domain ne doit jamais apprendre que le decoupage existe.
/// </summary>
public sealed class ChunkTests
{
    private static Chunk Sample(int ordinal = 2) =>
        new(
            Chunk.BuildId(DocumentId.From("retards-amendes"), ordinal),
            DocumentId.From("retards-amendes"),
            "Retards, relances et amendes",
            AccessLevel.Public,
            ordinal,
            "L'amende est de 0,10 euro par jour et par document.");

    [Fact]
    public void BuildId_CombinesDocumentIdAndOrdinal()
    {
        Assert.Equal("retards-amendes#3", Chunk.BuildId(DocumentId.From("retards-amendes"), 3));
    }

    [Fact]
    public void BuildId_IsStableForTheSameInputs()
    {
        // L'identifiant de morceau sert de cle d'upsert dans l'index : s'il n'etait pas
        // stable, une reindexation dupliquerait le corpus au lieu de le remplacer.
        var first = Chunk.BuildId(DocumentId.From("horaires-ouverture"), 0);
        var second = Chunk.BuildId(DocumentId.From("horaires-ouverture"), 0);

        Assert.Equal(first, second);
    }

    [Fact]
    public void ToEvidence_CarriesIdentityTitleTextLevelAndOrdinal()
    {
        var evidence = Sample().ToEvidence();

        Assert.Equal("retards-amendes", evidence.DocumentId.Value);
        Assert.Equal("Retards, relances et amendes", evidence.DocumentTitle);
        Assert.Equal("L'amende est de 0,10 euro par jour et par document.", evidence.Text);
        Assert.Equal(AccessLevel.Public, evidence.AccessLevel);
        Assert.Equal(2, evidence.Ordinal);
    }

    [Fact]
    public void ToEvidence_DropsTheChunkIdentifier()
    {
        // EvidenceFragment n'a pas de champ ChunkId, et c'est volontaire : la notion de
        // morceau est une decision de recherche. Le Domain raisonne sur des extraits de
        // documents, il n'a aucune raison de savoir comment on les a taillees.
        var evidence = Sample().ToEvidence();

        Assert.DoesNotContain("#", evidence.DocumentId.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void ToEvidence_CopiesTheDocumentAccessLevel_SoTheIndexNeedNotBeQueriedAgain()
    {
        var chunk = new Chunk(
            Chunk.BuildId(DocumentId.From("grille-remuneration"), 0),
            DocumentId.From("grille-remuneration"),
            "Grille de remuneration des agents",
            AccessLevel.Confidential,
            0,
            "Indice majore de reference.");

        // Le niveau voyage AVEC le fragment : la politique d'acces du Domain peut donc
        // trancher sans aller relire le document d'origine, donc sans dependance.
        Assert.Equal(AccessLevel.Confidential, chunk.ToEvidence().AccessLevel);
    }

    [Fact]
    public void Equality_IsByValue_LikeEveryApplicationModel()
    {
        Assert.Equal(Sample(), Sample());
        Assert.NotEqual(Sample(1), Sample(2));
    }

    // -----------------------------------------------------------------------
    // Ce qu'on vectorise n'est pas ce qu'on affiche
    // -----------------------------------------------------------------------

    /// <summary>
    /// LE MORCEAU SEUL NE DIT PAS DE QUOI IL PARLE. Le decoupage detache le paragraphe du
    /// titre qui le nommait ; un morceau de « Traitement interne des retards » peut ne
    /// contenir ni « traitement », ni « interne », ni « retards ». Vectoriser le seul
    /// texte rendait alors le document introuvable par son propre sujet, et aucun reglage
    /// de topK ou de seuil ne rattrape ce qui n'a jamais ete indexe.
    /// </summary>
    [Fact]
    public void EmbeddingText_CarriesDocumentIdentityAheadOfTheChunk()
    {
        var chunk = Sample();

        Assert.StartsWith("retards-amendes Retards, relances et amendes", chunk.EmbeddingText, StringComparison.Ordinal);
        Assert.Contains("L'amende est de 0,10 euro par jour", chunk.EmbeddingText, StringComparison.Ordinal);
    }

    /// <summary>
    /// L'ENRICHISSEMENT NE FUITE PAS DANS CE QUI EST MONTRE. Le titre entre dans le
    /// vecteur, pas dans l'extrait : si <c>Text</c> se mettait a le contenir, il
    /// apparaitrait dans le prompt, dans les citations et dans les instantanes, et
    /// l'usager lirait un titre recopie au milieu d'une phrase.
    /// </summary>
    [Fact]
    public void EmbeddingText_DoesNotContaminateTheDisplayedText()
    {
        var chunk = Sample();

        Assert.Equal("L'amende est de 0,10 euro par jour et par document.", chunk.Text);
        Assert.Equal(chunk.Text, chunk.ToEvidence().Text);
        Assert.DoesNotContain("retards-amendes", chunk.Text, StringComparison.Ordinal);
    }
}
