using System.Text;

using AssistantQR.Application.Ports;
using AssistantQR.Domain.Documents;

namespace AssistantQR.Infrastructure.Corpus;

/// <summary>
/// Adaptateur REEL de <see cref="IDocumentRepository"/> : le corpus est un dossier de
/// fichiers Markdown a en-tete YAML.
/// </summary>
/// <remarks>
/// TOUT CE QUE CETTE CLASSE SAIT EST CE QUE L'APPLICATION IGNORE : qu'il existe un
/// dossier, des fichiers, une extension, un encodage, un en-tete a decoder et un
/// vocabulaire francais a traduire. Aucune de ces notions ne franchit le port.
///
/// DEUX DECISIONS MERITENT D'ETRE JUSTIFIEES.
///
/// 1. LE TRI. <c>Directory.GetFiles</c> ne garantit aucun ordre d'enumeration : il
///    depend du systeme de fichiers, pas de la norme. Or l'ordre des documents fixe
///    l'ordre des morceaux, donc leur rang, donc — a egalite de score — l'ordre des
///    citations. Un projet dont le sujet est la reproductibilite ne peut pas laisser
///    trainer une source de variabilite aussi gratuite : on trie explicitement.
///
/// 2. LE CACHE. Le port promet « tout le corpus » a chaque appel, et le pipeline
///    l'appelle plusieurs fois. Relire vingt-trois fichiers a chaque question serait
///    absurde ; les garder en memoire est ici sans risque, le corpus etant un
///    artefact du depot et non une base vivante. La contrepartie est assumee :
///    modifier un fichier en cours d'execution n'a aucun effet avant redemarrage.
/// </remarks>
public sealed class FileSystemDocumentRepository : IDocumentRepository
{
    private readonly string _corpusDirectory;
    private readonly SemaphoreSlim _loadGate = new(1, 1);

    private IReadOnlyList<Document>? _cache;

    /// <summary>Construit le depot sur un dossier. Le dossier n'est lu qu'au premier appel.</summary>
    public FileSystemDocumentRepository(string corpusDirectory)
    {
        if (string.IsNullOrWhiteSpace(corpusDirectory))
        {
            throw new ArgumentException("Le dossier du corpus doit être renseigné.", nameof(corpusDirectory));
        }

        _corpusDirectory = corpusDirectory;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Document>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await LoadAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<Document?> FindAsync(DocumentId id, CancellationToken cancellationToken = default)
    {
        var documents = await LoadAsync(cancellationToken).ConfigureAwait(false);

        foreach (var document in documents)
        {
            if (document.Id.Equals(id))
            {
                return document;
            }
        }

        // Le port rend null plutot que de lever : un identifiant absent est une
        // reponse, pas une anomalie. C'est l'appelant qui decide si elle est fatale.
        return null;
    }

    private async Task<IReadOnlyList<Document>> LoadAsync(CancellationToken cancellationToken)
    {
        var cached = _cache;
        if (cached is not null)
        {
            return cached;
        }

        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cache is not null)
            {
                return _cache;
            }

            _cache = await ReadDirectoryAsync(cancellationToken).ConfigureAwait(false);
            return _cache;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private async Task<IReadOnlyList<Document>> ReadDirectoryAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_corpusDirectory))
        {
            // Le message donne le chemin ABSOLU : l'erreur la plus frequente est un
            // chemin relatif resolu depuis un repertoire de travail inattendu.
            throw new DirectoryNotFoundException(
                $"Le dossier du corpus est introuvable : « {ToDisplayPath(_corpusDirectory)} ». " +
                "Vérifiez le paramètre « CorpusDirectory » de la configuration.");
        }

        var files = Directory.GetFiles(_corpusDirectory, "*.md", SearchOption.AllDirectories);
        Array.Sort(files, StringComparer.Ordinal);

        var documents = new List<Document>(files.Length);
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileName = Path.GetFileName(file);

            // Un README explique le corpus, il n'en fait pas partie. L'exclure evite
            // de faire echouer la lecture entiere sur un fichier de documentation.
            if (string.Equals(fileName, "README.md", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var text = await File.ReadAllTextAsync(file, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            var document = FrontMatterParser.Parse(fileName, text);

            if (seen.TryGetValue(document.Id.Value, out var previousFile))
            {
                throw new InvalidDataException(
                    $"L'identifiant « {document.Id} » est déclaré deux fois : " +
                    $"« {previousFile} » et « {fileName} ».");
            }

            seen.Add(document.Id.Value, fileName);
            documents.Add(document);
        }

        documents.Sort(static (left, right) => string.CompareOrdinal(left.Id.Value, right.Id.Value));

        return documents;
    }

    private static string ToDisplayPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }
}
