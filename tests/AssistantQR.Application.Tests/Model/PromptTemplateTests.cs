using AssistantQR.Application.Model;

using Xunit;

namespace AssistantQR.Application.Tests.Model;

/// <summary>
/// Un prompt est du code au sens ou il commande le comportement du systeme, et de la
/// donnee au sens ou il se modifie sans recompiler. Ces tests verifient les deux
/// garde-fous qui rendent cette ambiguite tenable : un rendu strict (jamais de trou
/// dans le prompt envoye) et une empreinte fiable (jamais de doute sur la version
/// reellement utilisee).
/// </summary>
public sealed class PromptTemplateTests
{
    private static PromptTemplate Template(string body, params string[] required) =>
        new("answer-with-citations", "1.0.0", body, required);

    [Fact]
    public void Render_AllPlaceholdersProvided_SubstitutesEveryOccurrence()
    {
        var template = Template("Question : {{question}}\nExtraits :\n{{evidence}}\nRefus : {{question}}", "question", "evidence");

        var rendered = template.Render(new Dictionary<string, string>
        {
            ["question"] = "Horaires du samedi ?",
            ["evidence"] = "[horaires] Horaires (public)",
        });

        Assert.Equal(
            "Question : Horaires du samedi ?\nExtraits :\n[horaires] Horaires (public)\nRefus : Horaires du samedi ?",
            rendered);
    }

    [Fact]
    public void Render_ToleratesWhitespaceInsideBraces()
    {
        var template = Template("Bonjour {{ nom }}.", "nom");

        Assert.Equal("Bonjour Ada.", template.Render(new Dictionary<string, string> { ["nom"] = "Ada" }));
    }

    [Fact]
    public void Render_MissingRequiredPlaceholder_Throws()
    {
        var template = Template("Question : {{question}}\n{{evidence}}", "question", "evidence");

        var error = Assert.Throws<InvalidOperationException>(
            () => template.Render(new Dictionary<string, string> { ["question"] = "Horaires ?" }));

        // Le message doit nommer le gabarit ET l'emplacement manquant : un prompt
        // incomplet produit une reponse plausible et fausse, le pire des bogues.
        Assert.Contains("evidence", error.Message, StringComparison.Ordinal);
        Assert.Contains("answer-with-citations", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_UnresolvedPlaceholderRemainsInBody_Throws()
    {
        // Le corps contient un emplacement que personne n'a declare requis : sans la
        // seconde verification, on enverrait litteralement « {{oubli} } » au modele.
        var template = Template("Question : {{question}} / {{oubli}}", "question");

        var error = Assert.Throws<InvalidOperationException>(
            () => template.Render(new Dictionary<string, string> { ["question"] = "Horaires ?" }));

        Assert.Contains("{{oubli}}", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Fingerprint_IsTwelveLowercaseHexCharacters()
    {
        var fingerprint = Template("Corps du gabarit.").Fingerprint;

        Assert.Equal(12, fingerprint.Length);
        Assert.All(fingerprint, c => Assert.True(
            (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'),
            $"Caractere hexadecimal minuscule attendu, obtenu « {c} »."));
    }

    [Fact]
    public void Fingerprint_SameBody_IsStableAcrossInstancesAndCalls()
    {
        var first = Template("Corps identique.");
        var second = Template("Corps identique.");

        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(first.Fingerprint, first.Fingerprint);
    }

    [Fact]
    public void Fingerprint_OneCharacterDifference_ChangesTheFingerprint()
    {
        // C'est la propriete qui rend l'empreinte utile : on modifie un prompt sans
        // toujours penser a en changer la version. Le numero de version ment parfois,
        // l'empreinte jamais.
        var original = Template("Reponds en francais.");
        var edited = Template("Reponds en Francais.");

        Assert.NotEqual(original.Fingerprint, edited.Fingerprint);
    }

    [Fact]
    public void Fingerprint_AfterWithExpressionOnBody_IsRecomputed()
    {
        var original = Template("Corps initial.");
        var cached = original.Fingerprint;

        // Le champ de cache ne doit PAS etre recopie par le constructeur de copie du
        // record, sinon la nouvelle instance porterait l'empreinte de l'ancienne : le
        // cache deviendrait un mensonge, exactement au moment ou l'on veut la verite.
        var modified = original with { Body = "Corps modifie." };

        Assert.NotEqual(cached, modified.Fingerprint);
    }

    [Fact]
    public void Describe_ReturnsNameVersionAndFingerprint()
    {
        var template = Template("Corps.");

        var descriptor = template.Describe();

        Assert.Equal("answer-with-citations", descriptor.Name);
        Assert.Equal("1.0.0", descriptor.Version);
        Assert.Equal(template.Fingerprint, descriptor.Fingerprint);
    }
}
