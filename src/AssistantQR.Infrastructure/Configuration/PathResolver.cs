namespace AssistantQR.Infrastructure.Configuration;

/// <summary>
/// Transforme un chemin relatif de configuration (« corpus », « prompts ») en chemin
/// absolu utilisable, en cherchant la racine du depot.
/// </summary>
/// <remarks>
/// C'EST UN PIS-ALLER, ET IL FAUT LE DIRE. Une application correctement empaquetee ne
/// va pas chercher ses donnees en remontant l'arborescence jusqu'a tomber sur un fichier
/// de solution : elle recoit des chemins absolus de son environnement, ou embarque ses
/// ressources dans son propre repertoire de deploiement. Ce que fait cette classe serait
/// deplace en production.
///
/// POURQUOI ON LE FAIT QUAND MEME. Le corpus et les gabarits sont des artefacts du
/// DEPOT, pas de l'application : on veut pouvoir les modifier dans l'editeur et relancer,
/// sans etape de copie. Or le repertoire de travail d'un <c>dotnet run</c> depend d'ou
/// la commande a ete tapee, et <c>AppContext.BaseDirectory</c> pointe sur
/// <c>bin/Debug/net8.0</c>, cinq niveaux sous les donnees. Sans cette bidouille, la
/// premiere commande d'un etudiant echoue sur un « dossier introuvable » qui ne lui
/// apprend rien sur l'architecture.
///
/// LA CONTREPARTIE EST REELLE : le comportement du programme depend de la presence d'un
/// fichier de solution quelque part au-dessus de lui. Deplacez le binaire ailleurs, et
/// la resolution change. C'est le genre de dependance implicite au systeme de fichiers
/// qu'un projet serieux isole derriere un port ; ici, elle est concentree dans un seul
/// endroit, documentee, et n'est appelee qu'au montage — jamais depuis l'Application.
/// </remarks>
public static class PathResolver
{
    /// <summary>Le marqueur de racine : le fichier de solution du depot.</summary>
    private const string SolutionFileName = "AssistantQR.sln";

    /// <summary>Borne la remontee : au-dela, on cherche un depot qui n'existe pas.</summary>
    private const int MaxAncestors = 12;

    /// <summary>
    /// Rend un chemin absolu pour <paramref name="relativePath"/>. Un chemin deja absolu
    /// est rendu tel quel ; sinon on essaie d'abord le repertoire du binaire, puis la
    /// racine du depot.
    /// </summary>
    /// <remarks>
    /// La methode ne verifie pas que la cible existe au bout du compte : un dossier
    /// d'instantanes est cree a la premiere ecriture, et c'est a l'adaptateur concerne
    /// de dire, avec un chemin absolu dans son message, ce qu'il n'a pas trouve.
    /// </remarks>
    public static string Resolve(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("Le chemin à résoudre doit être renseigné.", nameof(relativePath));
        }

        var trimmed = relativePath.Trim();

        if (Path.IsPathRooted(trimmed))
        {
            return Path.GetFullPath(trimmed);
        }

        var fromBinary = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, trimmed));

        // Cas du deploiement : les donnees ont ete copiees a cote du binaire. C'est le
        // cas que l'on souhaite voir gagner, donc on l'essaie en premier.
        if (Exists(fromBinary))
        {
            return fromBinary;
        }

        var repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);

        // Cas du developpement : les donnees sont a la racine du depot, plusieurs
        // niveaux au-dessus du binaire.
        return repositoryRoot is null
            ? fromBinary
            : Path.GetFullPath(Path.Combine(repositoryRoot, trimmed));
    }

    private static bool Exists(string path) => Directory.Exists(path) || File.Exists(path);

    private static string? FindRepositoryRoot(string startDirectory)
    {
        var current = new DirectoryInfo(startDirectory);

        for (var depth = 0; current is not null && depth < MaxAncestors; depth++)
        {
            if (File.Exists(Path.Combine(current.FullName, SolutionFileName)))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }
}
