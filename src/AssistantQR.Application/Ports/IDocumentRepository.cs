using AssistantQR.Domain.Documents;

namespace AssistantQR.Application.Ports;

/// <summary>Acces au corpus documentaire.</summary>
/// <remarks>
/// POURQUOI LA FRONTIERE EST ICI.
/// Le cas d'usage a besoin d'une chose et d'une seule : la liste des documents, sous
/// la forme d'entites du Domain deja valides. Tout ce qui precede — un dossier de
/// fichiers Markdown, un en-tete YAML, une base SQL, une API de GED — est un detail
/// de stockage dont le pipeline n'a pas a connaitre l'existence.
///
/// POURQUOI PAS PLUS BAS. On aurait pu exposer un <c>Stream</c> ou un
/// <c>IEnumerable&lt;string&gt;</c> de chemins de fichiers : la frontiere aurait alors
/// fait fuir la notion de fichier dans l'Application, et le cas d'usage aurait du
/// savoir parser du YAML. Le decodage du format et la traduction du niveau d'acces
/// francais (« interne ») vers <c>AccessLevel.Internal</c> appartiennent a
/// l'adaptateur : ce sont des dialectes du monde exterieur.
///
/// POURQUOI PAS PLUS HAUT. On aurait pu exposer directement des <c>Chunk</c>, en
/// laissant le depot decouper. Ce serait une faute : le decoupage est une decision de
/// pertinence de recherche, elle doit rester visible et remplacable dans le pipeline,
/// pas enterree dans un lecteur de fichiers.
///
/// L'asynchronisme est dans la signature alors que l'implementation de test est
/// synchrone : une frontiere se dimensionne sur l'implementation la plus contraignante
/// (ici un appel reseau), pas sur la plus commode.
/// </remarks>
public interface IDocumentRepository
{
    /// <summary>Tout le corpus. La pagination serait un raffinement inutile a cette echelle.</summary>
    Task<IReadOnlyList<Document>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Un document precis, ou <c>null</c> s'il n'existe pas.</summary>
    Task<Document?> FindAsync(DocumentId id, CancellationToken cancellationToken = default);
}
