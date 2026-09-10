using AssistantQR.Domain.Access;
using AssistantQR.Domain.Answers;
using AssistantQR.Domain.Documents;
using AssistantQR.Domain.Evidence;
using Xunit;

namespace AssistantQR.Domain.Tests;

/// <summary>
/// Une citation recopie le titre et le niveau du document au lieu de pointer vers lui.
/// Cette duplication est assumee : une réponse doit rester verifiable telle quelle,
/// des mois plus tard, meme si le corpus a bouge entre-temps.
/// </summary>
public sealed class CitationTests
{
    private static EvidenceFragment Fragment(string text, int ordinal = 3) => new(
        DocumentId.From("gestion-retards-interne"),
        "Traitement interne des retards",
        text,
        AccessLevel.Internal,
        ordinal);

    [Fact]
    public void FromEvidence_FragmentQuelconque_RecopieIdentiteTitreNiveauEtOrdinal()
    {
        var citation = Citation.FromEvidence(Fragment("Une relance est envoyée au bout de sept jours."));

        Assert.Equal(DocumentId.From("gestion-retards-interne"), citation.DocumentId);
        Assert.Equal("Traitement interne des retards", citation.DocumentTitle);
        Assert.Equal(AccessLevel.Internal, citation.AccessLevel);
        Assert.Equal(3, citation.ChunkOrdinal);
    }

    [Fact]
    public void FromEvidence_TexteCourt_ConserveLeTexteIntegral()
    {
        var citation = Citation.FromEvidence(Fragment("Relance à sept jours."));

        Assert.Equal("Relance à sept jours.", citation.Excerpt);
        Assert.DoesNotContain("…", citation.Excerpt);
    }

    /// <summary>Longueur par defaut : 240 caracteres utiles, coupes sur une frontiere de mot.</summary>
    [Fact]
    public void FromEvidence_TexteTropLongEtLongueurParDefaut_TronqueA240()
    {
        var long_ = string.Join(" ", Enumerable.Repeat("mot", 200));

        var citation = Citation.FromEvidence(Fragment(long_));

        Assert.EndsWith("…", citation.Excerpt);
        Assert.True(
            citation.Excerpt.Length <= 241,
            $"L'extrait devrait tenir en 240 caractères plus les points de suspension, il en fait {citation.Excerpt.Length}.");
        Assert.StartsWith("mot mot mot", citation.Excerpt);
    }

    [Fact]
    public void FromEvidence_LongueurExplicite_TronqueSurLaFrontiereDeMot()
    {
        var citation = Citation.FromEvidence(Fragment("Le chat dort sur le tapis rouge."), 10);

        Assert.Equal("Le chat…", citation.Excerpt);
    }

    [Fact]
    public void FromEvidence_TexteAvecSautsDeLigne_NormaliseLesBlancs()
    {
        var citation = Citation.FromEvidence(Fragment("Première ligne.\n\n   Seconde\tligne."));

        Assert.Equal("Première ligne. Seconde ligne.", citation.Excerpt);
    }

    /// <summary>Value object : deux citations de meme contenu sont interchangeables.</summary>
    [Fact]
    public void Egalite_MemesChamps_CitationsEgales()
    {
        var premiere = Citation.FromEvidence(Fragment("Texte identique."));
        var seconde = Citation.FromEvidence(Fragment("Texte identique."));

        Assert.Equal(premiere, seconde);
        Assert.NotEqual(premiere, Citation.FromEvidence(Fragment("Texte identique.", ordinal: 4)));
    }
}
