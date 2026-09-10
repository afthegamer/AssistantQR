using AssistantQR.Domain.Access;
using AssistantQR.Domain.Documents;
using AssistantQR.Infrastructure.Corpus;
using AssistantQR.Infrastructure.Tests.Support;

using Xunit;

namespace AssistantQR.Infrastructure.Tests.Corpus;

/// <summary>
/// Tests de la lecture de l'en-tete YAML du corpus.
/// </summary>
/// <remarks>
/// POURQUOI CES TESTS PASSENT PAR LE DEPOT ET NON PAR LE PARSEUR DIRECTEMENT.
/// <c>FrontMatterParser</c> est <c>internal</c>, et le projet d'infrastructure ne
/// declare pas d'<c>InternalsVisibleTo</c>. Ce n'est pas une contrariete a contourner :
/// c'est la consequence assumee d'un choix de conception. Le parseur n'est pas un
/// service offert au systeme, c'est la mecanique interne d'un adaptateur ; le rendre
/// public pour le confort des tests reviendrait a elargir une surface publique pour une
/// raison etrangere au besoin des appelants.
///
/// On teste donc le comportement OBSERVABLE : ce que <c>FileSystemDocumentRepository</c>
/// rend, et ce qu'il refuse. La couverture est la meme, la contrainte de conception
/// reste intacte, et le test survit a une reecriture de la mecanique interne.
/// </remarks>
public sealed class FrontMatterParserTests
{
    [Fact]
    public async Task Parse_EnteteValide_RenvoieDocumentComplet()
    {
        using var directory = new TempDirectory("frontmatter-valide");

        var content = string.Join('\n',
            "---",
            "id: horaires-ouverture",
            "titre: Horaires d'ouverture au public",
            "niveau: public",
            "tags: [accueil, horaires]",
            "---",
            "",
            "La médiathèque ouvre du mardi au samedi.",
            "",
            "Le dimanche, elle reste fermée toute la journée.");

        var document = await ReadSingleAsync(directory, "horaires-ouverture.md", content);

        Assert.Equal(DocumentId.From("horaires-ouverture"), document.Id);
        Assert.Equal("Horaires d'ouverture au public", document.Title);
        Assert.Equal(AccessLevel.Public, document.AccessLevel);
        Assert.Equal(new[] { "accueil", "horaires" }, document.Tags);
        Assert.Contains("La médiathèque ouvre du mardi au samedi.", document.Content, StringComparison.Ordinal);

        // L'en-tete ne doit pas se retrouver dans le contenu indexe : il serait
        // decoupe, vectorise, puis recite au lecteur comme s'il etait du texte utile.
        Assert.DoesNotContain("niveau:", document.Content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("public", 0)]
    [InlineData("interne", 1)]
    [InlineData("confidentiel", 2)]
    [InlineData("Interne", 1)]
    [InlineData("  CONFIDENTIEL  ", 2)]
    public async Task Parse_NiveauFrancais_EstTraduitVersLeVocabulaireDuDomain(string french, int expectedRank)
    {
        using var directory = new TempDirectory("frontmatter-niveau");

        var content = string.Join('\n',
            "---",
            "id: note-interne",
            "titre: Une note quelconque",
            "niveau: " + french,
            "---",
            "",
            "Un contenu suffisant pour que le document soit valide.");

        var document = await ReadSingleAsync(directory, "note-interne.md", content);

        Assert.Equal(AccessLevel.FromRank(expectedRank), document.AccessLevel);
    }

    [Fact]
    public async Task Parse_NiveauInconnu_EchoueEnNommantLesValeursAttendues()
    {
        using var directory = new TempDirectory("frontmatter-niveau-inconnu");

        var content = string.Join('\n',
            "---",
            "id: note-interne",
            "titre: Une note quelconque",
            "niveau: secret-defense",
            "---",
            "",
            "Un contenu suffisant pour que le document soit valide.");

        var exception = await ReadFailureAsync(directory, "note-interne.md", content);

        Assert.Contains("secret-defense", exception.Message, StringComparison.Ordinal);
        Assert.Contains("public, interne ou confidentiel", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Parse_EnteteAbsent_EchoueEnNommantLeFichier()
    {
        using var directory = new TempDirectory("frontmatter-sans-entete");

        var content = "Un document sans le moindre en-tête YAML, juste du texte.";

        var exception = await ReadFailureAsync(directory, "sans-entete.md", content);

        Assert.Contains("sans-entete.md", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Parse_EnteteJamaisReferme_EchoueEnNommantLeFichier()
    {
        using var directory = new TempDirectory("frontmatter-non-referme");

        var content = string.Join('\n',
            "---",
            "id: entete-ouverte",
            "titre: Un titre",
            "niveau: public",
            "",
            "Le corps commence sans que l'en-tête ait été refermé.");

        var exception = await ReadFailureAsync(directory, "entete-ouverte.md", content);

        Assert.Contains("entete-ouverte.md", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Parse_TitreManquant_EchoueEnNommantLaCleEtLeFichier()
    {
        using var directory = new TempDirectory("frontmatter-sans-titre");

        var content = string.Join('\n',
            "---",
            "id: sans-titre",
            "niveau: public",
            "---",
            "",
            "Un contenu suffisant pour que le document soit valide.");

        var exception = await ReadFailureAsync(directory, "sans-titre.md", content);

        Assert.Contains("sans-titre.md", exception.Message, StringComparison.Ordinal);
        Assert.Contains("titre", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Parse_NiveauManquant_EchoueEnNommantLaCleEtLeFichier()
    {
        using var directory = new TempDirectory("frontmatter-sans-niveau");

        var content = string.Join('\n',
            "---",
            "id: sans-niveau",
            "titre: Un titre parfaitement valide",
            "---",
            "",
            "Un contenu suffisant pour que le document soit valide.");

        var exception = await ReadFailureAsync(directory, "sans-niveau.md", content);

        Assert.Contains("sans-niveau.md", exception.Message, StringComparison.Ordinal);
        Assert.Contains("niveau", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Parse_TagsAbsents_RenvoieUneListeVideEtNonNulle()
    {
        using var directory = new TempDirectory("frontmatter-sans-tags");

        var content = string.Join('\n',
            "---",
            "id: sans-tags",
            "titre: Un document sans étiquette",
            "niveau: public",
            "---",
            "",
            "Un contenu suffisant pour que le document soit valide.");

        var document = await ReadSingleAsync(directory, "sans-tags.md", content);

        Assert.NotNull(document.Tags);
        Assert.Empty(document.Tags);
    }

    [Fact]
    public async Task Parse_IdentifiantDifferentDuNomDeFichier_EstRefuse()
    {
        using var directory = new TempDirectory("frontmatter-id-divergent");

        // L'identifiant voyage jusque dans les citations « [identifiant] » imposees au
        // modele : le laisser diverger du nom de fichier rendrait toute verification
        // manuelle d'une reponse penible. Le refus est donc immediat.
        var content = string.Join('\n',
            "---",
            "id: un-autre-identifiant",
            "titre: Un titre parfaitement valide",
            "niveau: public",
            "---",
            "",
            "Un contenu suffisant pour que le document soit valide.");

        var exception = await ReadFailureAsync(directory, "nom-du-fichier.md", content);

        Assert.Contains("nom-du-fichier.md", exception.Message, StringComparison.Ordinal);
        Assert.Contains("un-autre-identifiant", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Parse_CorpsVide_EchoueEnNommantLeFichier()
    {
        using var directory = new TempDirectory("frontmatter-corps-vide");

        var content = string.Join('\n',
            "---",
            "id: corps-vide",
            "titre: Un titre parfaitement valide",
            "niveau: public",
            "---",
            "",
            "");

        var exception = await ReadFailureAsync(directory, "corps-vide.md", content);

        Assert.Contains("corps-vide.md", exception.Message, StringComparison.Ordinal);
    }

    private static async Task<Document> ReadSingleAsync(TempDirectory directory, string fileName, string content)
    {
        directory.Write(fileName, content);

        var repository = new FileSystemDocumentRepository(directory.FullPath);
        var documents = await repository.GetAllAsync();

        return Assert.Single(documents);
    }

    private static async Task<InvalidDataException> ReadFailureAsync(
        TempDirectory directory,
        string fileName,
        string content)
    {
        directory.Write(fileName, content);

        var repository = new FileSystemDocumentRepository(directory.FullPath);

        return await Assert.ThrowsAsync<InvalidDataException>(() => repository.GetAllAsync());
    }
}
