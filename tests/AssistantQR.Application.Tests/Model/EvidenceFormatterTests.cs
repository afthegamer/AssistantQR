using AssistantQR.Application.Model;

using AssistantQR.Domain.Access;
using AssistantQR.Domain.Documents;
using AssistantQR.Domain.Evidence;

using Xunit;

namespace AssistantQR.Application.Tests.Model;

/// <summary>
/// Le format du bloc d'extraits est un CONTRAT, pas une preference de mise en page.
/// Deux composants en dependent litteralement : le gabarit de prompt, qui promet au
/// modele que les identifiants a citer se trouvent entre crochets en tete de bloc, et
/// les modeles extractifs hors ligne (<c>ExtractiveLanguageModel</c> en Infrastructure,
/// <c>EchoingLanguageModel</c> ici) qui relisent ce format pour repondre. Le modifier
/// sans toucher a ces tests, c'est casser les deux en silence.
/// </summary>
public sealed class EvidenceFormatterTests
{
    private static ScoredFragment Fragment(string id, string title, string text, AccessLevel level, double score, int ordinal = 0) =>
        new(new EvidenceFragment(DocumentId.From(id), title, text, level, ordinal), score);

    [Fact]
    public void Format_SingleFragment_ProducesHeaderLineThenText()
    {
        var formatted = EvidenceFormatter.Format(new[]
        {
            Fragment("horaires-ouverture", "Horaires d'ouverture au public", "La mediatheque ouvre a 10 h.", AccessLevel.Public, 0.91),
        });

        Assert.Equal(
            "[horaires-ouverture] Horaires d'ouverture au public (public)\nLa mediatheque ouvre a 10 h.",
            formatted);
    }

    [Fact]
    public void Format_SeveralFragments_SeparatesBlocksWithABlankLine()
    {
        var formatted = EvidenceFormatter.Format(new[]
        {
            Fragment("horaires-ouverture", "Horaires", "Ouvert le samedi.", AccessLevel.Public, 0.90),
            Fragment("planning-agents", "Planning des agents", "Deux agents le samedi.", AccessLevel.Internal, 0.71),
        });

        Assert.Equal(
            "[horaires-ouverture] Horaires (public)\nOuvert le samedi.\n\n" +
            "[planning-agents] Planning des agents (internal)\nDeux agents le samedi.",
            formatted);
    }

    [Fact]
    public void Format_AccessLevelIsWrittenInEnglish_TheDomainCanonicalName()
    {
        // La traduction francaise (« interne », « confidentiel ») appartient a
        // l'Infrastructure qui lit le corpus. Ce qui part vers le modele est le nom
        // canonique du Domain : une seule orthographe a travers tout le pipeline.
        var formatted = EvidenceFormatter.Format(new[]
        {
            Fragment("grille-remuneration", "Grille de remuneration", "Indice majore.", AccessLevel.Confidential, 0.55),
        });

        Assert.Contains("(confidential)", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_TrimsFragmentText_ButKeepsInnerLineBreaks()
    {
        var formatted = EvidenceFormatter.Format(new[]
        {
            Fragment("pret-documents", "Pret", "  Premiere ligne.\nSeconde ligne.  ", AccessLevel.Public, 0.8),
        });

        Assert.Equal(
            "[pret-documents] Pret (public)\nPremiere ligne.\nSeconde ligne.",
            formatted);
    }

    [Fact]
    public void Format_UsesLineFeedOnly_NotTheEnvironmentNewLine()
    {
        // Un instantane enregistre sous Windows doit se comparer a un instantane
        // enregistre sous Linux. Un « \r\n » ici ferait apparaitre une derive sur
        // chaque question au seul motif d'un changement de machine.
        var formatted = EvidenceFormatter.Format(new[]
        {
            Fragment("a", "A", "Texte A.", AccessLevel.Public, 0.9),
            Fragment("b", "B", "Texte B.", AccessLevel.Public, 0.8),
        });

        Assert.DoesNotContain("\r", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_EmptyList_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, EvidenceFormatter.Format(Array.Empty<ScoredFragment>()));
    }

    [Fact]
    public void Format_DoesNotLeakTheScore()
    {
        // Le score est un artefact de la mecanique de recherche. Le montrer au modele
        // l'inviterait a raisonner dessus — a « faire confiance » au premier extrait —
        // alors que la seule chose qu'on lui demande est de citer ce qu'il utilise.
        var formatted = EvidenceFormatter.Format(new[]
        {
            Fragment("horaires-ouverture", "Horaires", "Ouvert le samedi.", AccessLevel.Public, 0.9137),
        });

        Assert.DoesNotContain("0.91", formatted, StringComparison.Ordinal);
    }
}
