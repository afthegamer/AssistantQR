using AssistantQR.Infrastructure.Configuration;
using AssistantQR.Infrastructure.Tests.Support;

using Xunit;

namespace AssistantQR.Infrastructure.Tests.Configuration;

/// <summary>
/// Tests de la resolution des chemins de configuration.
/// </summary>
/// <remarks>
/// ON TESTE UN PIS-ALLER, ET C'EST ASSUME. Une application correctement empaquetee ne
/// remonte pas l'arborescence jusqu'a tomber sur un fichier de solution. Le depot le
/// fait quand meme, parce que le corpus et les gabarits sont des artefacts du DEPOT et
/// qu'on veut pouvoir les modifier dans l'editeur puis relancer sans etape de copie.
///
/// Ces tests ont donc une valeur particuliere : ils documentent une dependance
/// implicite au systeme de fichiers. Le jour ou quelqu'un deplacera le binaire, c'est
/// ici que l'echec apparaitra — et le commentaire lui dira pourquoi.
/// </remarks>
public sealed class PathResolverTests
{
    [Fact]
    public void Resolve_CheminRelatifDuDepot_TrouveLeDossierReel()
    {
        var resolved = PathResolver.Resolve("corpus");

        Assert.True(Path.IsPathRooted(resolved), "Le chemin rendu doit être absolu.");
        Assert.True(Directory.Exists(resolved), $"Le dossier « {resolved} » devrait exister.");
    }

    [Theory]
    [InlineData("corpus")]
    [InlineData("prompts")]
    [InlineData("snapshots")]
    public void Resolve_DossiersDeDonneesDuDepot_SontTousTrouves(string relative)
    {
        // Ce sont exactement les trois chemins que le montage resout au demarrage : si
        // l'un d'eux echouait, la premiere commande d'un etudiant echouerait sur un
        // « dossier introuvable » qui ne lui apprendrait rien sur l'architecture.
        Assert.True(Directory.Exists(PathResolver.Resolve(relative)));
    }

    [Fact]
    public void Resolve_CheminDejaAbsolu_EstRenduTelQuel()
    {
        using var directory = new TempDirectory("chemin-absolu");

        var resolved = PathResolver.Resolve(directory.FullPath);

        Assert.Equal(Path.GetFullPath(directory.FullPath), resolved);
    }

    [Fact]
    public void Resolve_CibleInexistante_RendQuandMemeUnCheminAbsolu()
    {
        var resolved = PathResolver.Resolve("dossier-qui-n-existe-nulle-part");

        // La methode ne verifie pas que la cible existe : un dossier d'instantanes est
        // cree a la premiere ecriture, et c'est a l'adaptateur concerne de dire, avec un
        // chemin absolu dans son message, ce qu'il n'a pas trouve.
        Assert.True(Path.IsPathRooted(resolved));
        Assert.EndsWith("dossier-qui-n-existe-nulle-part", resolved, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_CheminNonRenseigne_EstRefuse(string relative)
    {
        Assert.Throws<ArgumentException>(() => PathResolver.Resolve(relative));
    }

    [Fact]
    public void Resolve_MemeChemin_RendDeuxFoisLeMemeResultat()
    {
        Assert.Equal(PathResolver.Resolve("corpus"), PathResolver.Resolve("corpus"));
    }
}
