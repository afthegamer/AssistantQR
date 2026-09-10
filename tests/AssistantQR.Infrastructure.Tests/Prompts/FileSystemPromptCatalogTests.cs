using AssistantQR.Infrastructure.Prompts;
using AssistantQR.Infrastructure.Tests.Support;

using Xunit;

namespace AssistantQR.Infrastructure.Tests.Prompts;

/// <summary>
/// Tests du catalogue de gabarits sur disque.
/// </summary>
/// <remarks>
/// LA CONVENTION DE NOMMAGE EST LE SUJET. Mettre la version dans le NOM du fichier fait
/// coexister deux versions d'un prompt sur le disque : on peut rejouer le meme jeu de
/// questions sous 1.0.0 puis sous 1.1.0 et comparer. Un fichier unique qu'on modifie en
/// place rendrait cette comparaison impossible — l'ancienne version aurait disparu au
/// moment precis ou l'on en aurait eu besoin. Les tests de <c>GetLatest</c> verifient
/// donc une propriete d'outillage, pas un detail de tri.
///
/// Le dernier test porte sur les VRAIS gabarits du depot : un prompt est une entree du
/// systeme au meme titre que le code, et les placeholders qu'il declare sont un contrat
/// que le cas d'usage doit pouvoir honorer.
/// </remarks>
public sealed class FileSystemPromptCatalogTests
{
    [Fact]
    public void Get_NomEtVersionConnus_RendLeGabarit()
    {
        using var directory = BuildCatalogDirectory();

        var catalog = new FileSystemPromptCatalog(directory.FullPath);
        var template = catalog.Get("essai", "1.0.0");

        Assert.Equal("essai", template.Name);
        Assert.Equal("1.0.0", template.Version);
        Assert.Contains("{{question}}", template.Body, StringComparison.Ordinal);
        Assert.Equal(new[] { "question", "evidence" }, template.RequiredPlaceholders);
    }

    [Fact]
    public void GetLatest_DeuxVersions_RendLaPlusRecenteAuSensSemantique()
    {
        using var directory = BuildCatalogDirectory();

        var catalog = new FileSystemPromptCatalog(directory.FullPath);

        Assert.Equal("1.1.0", catalog.GetLatest("essai").Version);
    }

    [Fact]
    public void GetLatest_VersionsNumeriquementProches_NeSeTriePasAlphabetiquement()
    {
        using var directory = new TempDirectory("prompts-tri-semantique");
        directory.Write("essai@1.9.0.md", BuildTemplateFile("essai", "1.9.0"));
        directory.Write("essai@1.10.0.md", BuildTemplateFile("essai", "1.10.0"));

        var catalog = new FileSystemPromptCatalog(directory.FullPath);

        // « 1.10.0 » vient APRES « 1.9.0 », ce qu'un tri de chaines aurait inverse.
        Assert.Equal("1.10.0", catalog.GetLatest("essai").Version);
    }

    [Fact]
    public void Get_VersionAbsente_LeveEnListantLeCatalogue()
    {
        using var directory = BuildCatalogDirectory();

        var catalog = new FileSystemPromptCatalog(directory.FullPath);

        var exception = Assert.Throws<InvalidOperationException>(() => catalog.Get("essai", "2.0.0"));

        Assert.Contains("essai@2.0.0", exception.Message, StringComparison.Ordinal);
        Assert.Contains("essai@1.0.0", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetLatest_NomAbsent_Leve()
    {
        using var directory = BuildCatalogDirectory();

        var catalog = new FileSystemPromptCatalog(directory.FullPath);

        Assert.Throws<InvalidOperationException>(() => catalog.GetLatest("gabarit-inexistant"));
    }

    [Fact]
    public void List_DossierTemporaire_DecritTousLesGabaritsChargres()
    {
        using var directory = BuildCatalogDirectory();

        var catalog = new FileSystemPromptCatalog(directory.FullPath);
        var descriptors = catalog.List();

        Assert.Equal(2, descriptors.Count);
        Assert.All(descriptors, descriptor => Assert.Equal(12, descriptor.Fingerprint.Length));
    }

    [Fact]
    public void Constructeur_ReadmeSansArobase_EstIgnoreSansEchouer()
    {
        using var directory = BuildCatalogDirectory();
        directory.Write("README.md", "# Gabarits\n\nConvention de nommage : nom@version.md.");

        var catalog = new FileSystemPromptCatalog(directory.FullPath);

        Assert.Equal(2, catalog.List().Count);
    }

    [Fact]
    public void Constructeur_DossierAbsent_LeveAvecUnMessageFrancais()
    {
        using var directory = new TempDirectory("prompts-absent");
        var missing = directory.Combine("dossier-qui-n-existe-pas");

        var exception = Assert.Throws<DirectoryNotFoundException>(() => new FileSystemPromptCatalog(missing));

        Assert.Contains("introuvable", exception.Message, StringComparison.Ordinal);
        Assert.Contains("PromptsDirectory", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructeur_DossierSansGabarit_EstRefuse()
    {
        using var directory = new TempDirectory("prompts-vide");

        // Un gabarit absent est une erreur de configuration : elle doit interrompre le
        // demarrage, pas surgir a la premiere question d'un utilisateur.
        Assert.Throws<InvalidDataException>(() => new FileSystemPromptCatalog(directory.FullPath));
    }

    [Fact]
    public void Constructeur_EnteteEnDesaccordAvecLeNomDuFichier_EstRefuse()
    {
        using var directory = new TempDirectory("prompts-incoherent");
        directory.Write("essai@1.0.0.md", BuildTemplateFile("essai", "1.1.0"));

        // Un prompt qui se declare 1.0.0 dans un fichier nomme 1.1.0 rendrait toute
        // empreinte de configuration mensongere.
        var exception = Assert.Throws<InvalidDataException>(() => new FileSystemPromptCatalog(directory.FullPath));

        Assert.Contains("essai@1.0.0.md", exception.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------
    // Tests portant sur les VRAIS gabarits du depot.
    // ------------------------------------------------------------------------

    [Theory]
    [InlineData("answer-with-citations", "1.0.0")]
    [InlineData("answer-with-citations", "1.1.0")]
    [InlineData("refusal-explanation", "1.0.0")]
    public void Get_GabaritsDuDepot_LesTroisGabaritsImposesSontPresents(string name, string version)
    {
        var catalog = new FileSystemPromptCatalog(RepositoryLayout.PromptsDirectory);

        var template = catalog.Get(name, version);

        Assert.Equal(name, template.Name);
        Assert.Equal(version, template.Version);
        Assert.False(string.IsNullOrWhiteSpace(template.Body));
    }

    [Fact]
    public void Get_AnswerWithCitations_DeclareLesTroisPlaceholdersDuContrat()
    {
        var catalog = new FileSystemPromptCatalog(RepositoryLayout.PromptsDirectory);

        var template = catalog.Get("answer-with-citations", "1.0.0");

        // Ce sont exactement les trois cles que le cas d'usage fournit au rendu : un
        // gabarit qui en declarerait une quatrieme ferait echouer chaque question.
        Assert.Contains("question", template.RequiredPlaceholders);
        Assert.Contains("evidence", template.RequiredPlaceholders);
        Assert.Contains("refusal_marker", template.RequiredPlaceholders);
        Assert.Equal(3, template.RequiredPlaceholders.Count);
    }

    [Fact]
    public void Render_AnswerWithCitations_ResoudTousLesPlaceholdersDuCorps()
    {
        var catalog = new FileSystemPromptCatalog(RepositoryLayout.PromptsDirectory);
        var template = catalog.Get("answer-with-citations", "1.0.0");

        var prompt = template.Render(new Dictionary<string, string>
        {
            ["question"] = "Quels sont les horaires du samedi ?",
            ["evidence"] = "[horaires-ouverture] Horaires d'ouverture au public (public)\nOuvert de 10 h à 19 h.",
            ["refusal_marker"] = "AUCUNE_REPONSE",
        });

        Assert.Contains("Quels sont les horaires du samedi ?", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// Ce test se casse a chaque nouvelle version publiee, et c'est voulu : ajouter un
    /// gabarit deplace la version « la plus recente », donc le comportement par defaut de
    /// quiconque appelle GetLatest. Un echec ici n'est pas une nuisance, c'est le rappel
    /// qu'une version de plus dans un dossier change ce que le systeme fait.
    ///
    /// 1.2.0 est arrivee au premier contact avec un vrai modele local : granite4.2:3b
    /// repondait ET ajoutait le marqueur de refus derriere sa reponse, avec les gabarits
    /// 1.0.0 comme 1.1.0. Le depot interdisant de modifier un gabarit publie, on en a
    /// ecrit un nouveau.
    /// </summary>
    [Fact]
    public void GetLatest_AnswerWithCitations_RendLaVersionLaPlusRecenteDuDepot()
    {
        var catalog = new FileSystemPromptCatalog(RepositoryLayout.PromptsDirectory);

        Assert.Equal("1.2.0", catalog.GetLatest("answer-with-citations").Version);
    }

    private static TempDirectory BuildCatalogDirectory()
    {
        var directory = new TempDirectory("prompts-catalogue");

        directory.Write("essai@1.0.0.md", BuildTemplateFile("essai", "1.0.0"));
        directory.Write("essai@1.1.0.md", BuildTemplateFile("essai", "1.1.0"));

        return directory;
    }

    private static string BuildTemplateFile(string name, string version) =>
        string.Join('\n',
            "---",
            "name: " + name,
            "version: " + version,
            "description: Un gabarit d'essai.",
            "placeholders: [question, evidence]",
            "---",
            "",
            "Voici les extraits : {{evidence}}",
            "",
            "Question : {{question}}");
}
