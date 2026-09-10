using AssistantQR.Domain.Access;
using AssistantQR.Domain.Documents;
using AssistantQR.Infrastructure.Corpus;
using AssistantQR.Infrastructure.Tests.Support;

using Xunit;

namespace AssistantQR.Infrastructure.Tests.Corpus;

/// <summary>
/// Tests de l'adaptateur reel du port <c>IDocumentRepository</c>.
/// </summary>
/// <remarks>
/// Deux familles de tests cohabitent ici, et la distinction merite d'etre vue.
/// Les premiers montent un corpus JETABLE dans un dossier temporaire : ils testent le
/// COMPORTEMENT de l'adaptateur, et resteraient valables si le corpus du depot etait
/// entierement reecrit. Les derniers lisent le VRAI dossier <c>corpus/</c> : ils
/// testent les DONNEES du depot, exactement comme un test de schema verifie une base.
/// Melanger les deux dans un meme test rendrait un echec ambigu ; les separer permet
/// de savoir immediatement si c'est le code ou le corpus qui a bouge.
/// </remarks>
public sealed class FileSystemDocumentRepositoryTests
{
    /// <summary>Nombre de documents impose par le contrat du depot.</summary>
    /// <remarks>
    /// CE CHIFFRE EST CENSE CASSER. Toute evolution du corpus — un document ajoute pour
    /// creer une contradiction de version, un document retire — fait echouer ce test, et
    /// c'est exactement ce qu'on lui demande. Le corpus est une DONNEE du systeme, au meme
    /// titre qu'un schema de base : on ne le modifie pas sans que quelqu'un le constate.
    /// Un echec ici n'est donc pas une regression a corriger dans le code, c'est une
    /// question a trancher — le changement de corpus etait-il voulu ? Si oui, on met la
    /// constante a jour dans le meme commit que les fichiers ajoutes.
    /// </remarks>
    private const int ExpectedCorpusSize = 27;

    [Fact]
    public async Task GetAllAsync_CorpusTemporaire_LitTousLesDocumentsAvecLeursNiveaux()
    {
        using var directory = new TempDirectory("corpus-trois-niveaux");
        WriteThreeLevelCorpus(directory);

        var repository = new FileSystemDocumentRepository(directory.FullPath);
        var documents = await repository.GetAllAsync();

        Assert.Equal(3, documents.Count);

        // Le tri par identifiant est explicite dans l'adaptateur : sans lui, l'ordre
        // dependrait du systeme de fichiers, donc l'ordre des morceaux, donc — a
        // egalite de score — l'ordre des citations.
        Assert.Equal(
            new[] { "confidentiel-doc", "interne-doc", "public-doc" },
            documents.Select(document => document.Id.Value));

        Assert.Equal(AccessLevel.Confidential, documents[0].AccessLevel);
        Assert.Equal(AccessLevel.Internal, documents[1].AccessLevel);
        Assert.Equal(AccessLevel.Public, documents[2].AccessLevel);
    }

    [Fact]
    public async Task GetAllAsync_ReadmeDansLeDossier_LIgnoreSansEchouer()
    {
        using var directory = new TempDirectory("corpus-avec-readme");
        WriteThreeLevelCorpus(directory);

        // Un README explique le corpus, il n'en fait pas partie — et il n'a pas
        // d'en-tete YAML : sans exclusion, il ferait echouer la lecture entiere.
        directory.Write("README.md", "# Corpus\n\nCe dossier contient les documents.");

        var repository = new FileSystemDocumentRepository(directory.FullPath);
        var documents = await repository.GetAllAsync();

        Assert.Equal(3, documents.Count);
    }

    [Fact]
    public async Task FindAsync_IdentifiantConnu_RenvoieLeDocument()
    {
        using var directory = new TempDirectory("corpus-find");
        WriteThreeLevelCorpus(directory);

        var repository = new FileSystemDocumentRepository(directory.FullPath);
        var document = await repository.FindAsync(DocumentId.From("interne-doc"));

        Assert.NotNull(document);
        Assert.Equal(AccessLevel.Internal, document!.AccessLevel);
    }

    [Fact]
    public async Task FindAsync_IdentifiantInconnu_RenvoieNull()
    {
        using var directory = new TempDirectory("corpus-find-absent");
        WriteThreeLevelCorpus(directory);

        var repository = new FileSystemDocumentRepository(directory.FullPath);

        // Le port rend null plutot que de lever : un identifiant absent est une
        // reponse, pas une anomalie. C'est l'appelant qui decide si elle est fatale.
        Assert.Null(await repository.FindAsync(DocumentId.From("document-inexistant")));
    }

    [Fact]
    public async Task GetAllAsync_DossierAbsent_LeveAvecUnMessageFrancaisEtLeCheminAbsolu()
    {
        using var directory = new TempDirectory("corpus-absent");
        var missing = directory.Combine("dossier-qui-n-existe-pas");

        var repository = new FileSystemDocumentRepository(missing);

        var exception = await Assert.ThrowsAsync<DirectoryNotFoundException>(() => repository.GetAllAsync());

        Assert.Contains("introuvable", exception.Message, StringComparison.Ordinal);
        Assert.Contains("CorpusDirectory", exception.Message, StringComparison.Ordinal);

        // Le chemin absolu est la moitie de la reponse : l'erreur la plus frequente est
        // un chemin relatif resolu depuis un repertoire de travail inattendu.
        Assert.Contains(Path.GetFullPath(missing), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetAllAsync_DeuxFichiersMemeIdentifiant_EstRefuse()
    {
        using var directory = new TempDirectory("corpus-doublon");

        directory.Write("doublon.md", BuildDocument("doublon", "Premier", "public"));
        directory.Write("sous-dossier/doublon.md", BuildDocument("doublon", "Second", "public"));

        var repository = new FileSystemDocumentRepository(directory.FullPath);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => repository.GetAllAsync());

        Assert.Contains("doublon", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetAllAsync_AppeleDeuxFois_RendLaMemeInstance()
    {
        using var directory = new TempDirectory("corpus-cache");
        WriteThreeLevelCorpus(directory);

        var repository = new FileSystemDocumentRepository(directory.FullPath);

        var first = await repository.GetAllAsync();
        var second = await repository.GetAllAsync();

        // Le cache est un choix documente : relire le corpus a chaque question serait
        // absurde. Sa contrepartie — une modification en cours d'execution reste
        // invisible — est verifiee ici, pour qu'elle soit constatee et non subie.
        Assert.Same(first, second);
    }

    // ------------------------------------------------------------------------
    // Tests portant sur le VRAI corpus du depot.
    // ------------------------------------------------------------------------

    [Fact]
    public async Task GetAllAsync_CorpusDuDepot_ContientLesDocumentsImposes()
    {
        var repository = new FileSystemDocumentRepository(RepositoryLayout.CorpusDirectory);

        var documents = await repository.GetAllAsync();

        Assert.Equal(ExpectedCorpusSize, documents.Count);
    }

    [Fact]
    public async Task GetAllAsync_CorpusDuDepot_ChaqueIdentifiantEgaleLeNomDeFichier()
    {
        var corpusDirectory = RepositoryLayout.CorpusDirectory;

        var repository = new FileSystemDocumentRepository(corpusDirectory);
        var documents = await repository.GetAllAsync();

        var fileStems = Directory
            .GetFiles(corpusDirectory, "*.md", SearchOption.AllDirectories)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(stem => !string.Equals(stem, "README", StringComparison.OrdinalIgnoreCase))
            .OrderBy(stem => stem, StringComparer.Ordinal)
            .ToArray();

        var identifiers = documents
            .Select(document => document.Id.Value)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(fileStems, identifiers);
    }

    [Fact]
    public async Task GetAllAsync_CorpusDuDepot_CouvreLesTroisNiveauxDAcces()
    {
        var repository = new FileSystemDocumentRepository(RepositoryLayout.CorpusDirectory);
        var documents = await repository.GetAllAsync();

        // Un corpus qui ne porterait qu'un seul niveau rendrait toute la demonstration
        // du controle d'acces — et l'ecart pre/post-filtrage — inobservable.
        Assert.Contains(documents, document => document.AccessLevel == AccessLevel.Public);
        Assert.Contains(documents, document => document.AccessLevel == AccessLevel.Internal);
        Assert.Contains(documents, document => document.AccessLevel == AccessLevel.Confidential);
    }

    private static void WriteThreeLevelCorpus(TempDirectory directory)
    {
        directory.Write("public-doc.md", BuildDocument("public-doc", "Un document public", "public"));
        directory.Write("interne-doc.md", BuildDocument("interne-doc", "Un document interne", "interne"));
        directory.Write("confidentiel-doc.md", BuildDocument("confidentiel-doc", "Un document confidentiel", "confidentiel"));
    }

    private static string BuildDocument(string id, string title, string level) =>
        string.Join('\n',
            "---",
            "id: " + id,
            "titre: " + title,
            "niveau: " + level,
            "tags: [essai]",
            "---",
            "",
            "Ce document parle des retards, des relances et des usagers de la médiathèque.",
            "",
            "Il contient assez de texte pour être découpé et indexé sans difficulté.");
}
