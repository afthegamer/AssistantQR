using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

using AssistantQR.Application.Ports;
using AssistantQR.Application.UseCases.Snapshots;

namespace AssistantQR.Infrastructure.Snapshots;

/// <summary>
/// Adaptateur REEL de <see cref="ISnapshotStore"/> : un instantane par fichier JSON.
/// </summary>
/// <remarks>
/// TOUTE LA SERIALISATION EST ICI, ET NULLE PART AILLEURS. Aucun attribut de
/// <c>System.Text.Json</c> ne figure sur les types de l'Application : ils sont des
/// enregistrements ordinaires, que cette classe sait ecrire. Si demain les instantanes
/// partaient dans une base ou dans un format binaire, aucun type metier ne bougerait.
/// C'est la difference entre « l'Application connait un format » et « un adaptateur
/// connait un format ».
///
/// LES OPTIONS DE SERIALISATION SONT DES CHOIX, PAS DES REGLAGES PAR DEFAUT.
/// <list type="bullet">
/// <item><description><c>WriteIndented</c> : un instantane se relit a l'oeil et se
/// compare avec <c>git diff</c>. Une ligne unique de trente mille caracteres rendrait
/// la revue impossible, ce qui viderait la methode de son interet.</description></item>
/// <item><description><c>JsonStringEnumConverter</c> : un motif de refus enregistre
/// comme <c>3</c> deviendrait faux le jour ou l'on insere une valeur dans
/// l'enumeration. Le nom, lui, survit au reordonnancement.</description></item>
/// <item><description><c>UnsafeRelaxedJsonEscaping</c> : sans lui, « médiathèque »
/// s'ecrit <c>médiathèque</c>. Le qualificatif « unsafe » vise l'injection
/// HTML — hors sujet pour un fichier destine a etre lu par un humain et par
/// <c>git diff</c>.</description></item>
/// </list>
///
/// LIMITE ASSUMEE : le dossier <c>snapshots/</c> heberge aussi le jeu de questions et
/// le fichier de rejeu, qui ne sont pas des instantanes. <see cref="ListAsync"/> les
/// listera donc. Filtrer sur des noms connus serait cacher le probleme ; le vrai
/// remede serait un sous-dossier, que la structure imposee du depot ne prevoit pas.
/// </remarks>
public sealed partial class JsonFileSnapshotStore : ISnapshotStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _directory;

    /// <summary>Construit le magasin sur un dossier, cree a la premiere ecriture.</summary>
    public JsonFileSnapshotStore(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("Le dossier des instantanés doit être renseigné.", nameof(directory));
        }

        _directory = directory;
    }

    /// <inheritdoc />
    public async Task SaveAsync(EvaluationSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        Directory.CreateDirectory(_directory);

        var path = BuildPath(snapshot.Name);

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, snapshot, SerializerOptions, cancellationToken)
                            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<EvaluationSnapshot?> LoadAsync(string name, CancellationToken cancellationToken = default)
    {
        var path = BuildPath(name);

        if (!File.Exists(path))
        {
            // Absence n'est pas anomalie : c'est l'appelant qui decide si un instantane
            // de reference manquant est une erreur ou une premiere execution.
            return null;
        }

        await using var stream = File.OpenRead(path);

        try
        {
            return await JsonSerializer.DeserializeAsync<EvaluationSnapshot>(stream, SerializerOptions, cancellationToken)
                                       .ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            // Un fichier corrompu est un probleme de stockage, pas de metier : on le
            // retraduit en exception d'entree-sortie, en nommant le fichier fautif.
            throw new InvalidDataException(
                $"L'instantané « {path} » n'est pas un JSON exploitable : {exception.Message}", exception);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_directory))
        {
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }

        var files = Directory.GetFiles(_directory, "*.json", SearchOption.TopDirectoryOnly);
        var names = new List<string>(files.Length);

        foreach (var file in files)
        {
            names.Add(Path.GetFileNameWithoutExtension(file));
        }

        names.Sort(StringComparer.Ordinal);

        return Task.FromResult<IReadOnlyList<string>>(names);
    }

    /// <summary>
    /// Transforme un nom d'instantane en nom de fichier sur. Le nom vient d'une ligne
    /// de commande : sans assainissement, « ../../etc/passwd » designerait un chemin
    /// hors du dossier prevu.
    /// </summary>
    internal static string SanitizeName(string name)
    {
        var sanitized = UnsafeCharacters().Replace(name?.Trim() ?? string.Empty, "_");

        if (sanitized.Length == 0)
        {
            throw new ArgumentException("Le nom d'un instantané ne peut pas être vide.", nameof(name));
        }

        return sanitized;
    }

    private string BuildPath(string name) => Path.Combine(_directory, SanitizeName(name) + ".json");

    [GeneratedRegex("[^a-zA-Z0-9._-]", RegexOptions.CultureInvariant)]
    private static partial Regex UnsafeCharacters();
}
