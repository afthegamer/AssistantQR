using System.Text.Json;
using System.Text.Json.Serialization;

using AssistantQR.Application.UseCases.Snapshots;

using AssistantQR.Infrastructure.Configuration;

namespace AssistantQR.Cli.Commands;

/// <summary>
/// Lecture du jeu de questions d'evaluation, un fichier JSON du depot.
/// </summary>
/// <remarks>
/// POURQUOI CETTE LECTURE EST DANS LA PRESENTATION ET NON DERRIERE UN PORT. Le jeu de
/// questions n'est pas une donnee du systeme : c'est un ARGUMENT de la commande, au meme
/// titre que la question tapee entre guillemets. L'utilisateur choisit son fichier avec
/// <c>--questions</c> ; le cas d'usage, lui, recoit une liste de
/// <see cref="QuestionSetItem"/> et ignore tout de son origine. Ajouter un neuvieme port
/// pour cela ferait entrer dans l'Application une notion — « le fichier passe en ligne de
/// commande » — qui n'appartient qu'au terminal.
///
/// LE FICHIER CONTIENT DES CHAMPS QUE CE TYPE IGNORE. Le jeu de questions du depot porte
/// une note pedagogique par entree, qui explique ce que chaque cas doit demontrer. Elle ne
/// sert pas a l'execution : l'analyseur JSON laisse tomber ce qu'il ne connait pas, et
/// c'est la bonne politique — un fichier de donnees a le droit d'etre plus riche que le
/// besoin du programme.
/// </remarks>
internal static class QuestionSet
{
    /// <summary>Nom du jeu de questions par defaut, dans le dossier des instantanes.</summary>
    public const string DefaultFileName = "questions.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Charge le jeu de questions designe, ou celui du depot si aucun n'est demande.</summary>
    /// <exception cref="FileNotFoundException">Si le fichier est introuvable.</exception>
    /// <exception cref="InvalidDataException">Si le fichier n'a pas la forme attendue.</exception>
    public static async Task<IReadOnlyList<QuestionSetItem>> LoadAsync(
        AssistantOptions options,
        string? requestedPath,
        CancellationToken cancellationToken)
    {
        var path = ResolvePath(options, requestedPath);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Jeu de questions introuvable : « {path} ». " +
                "Indique un autre fichier avec --questions, ou verifie le dossier des instantanes.",
                path);
        }

        await using var stream = File.OpenRead(path);

        List<QuestionEntry>? entries;
        try
        {
            entries = await JsonSerializer
                .DeserializeAsync<List<QuestionEntry>>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Le fichier « {path} » n'est pas un tableau JSON lisible : {exception.Message}", exception);
        }

        if (entries is null || entries.Count == 0)
        {
            throw new InvalidDataException(
                $"Le fichier « {path} » ne contient aucune question. " +
                "Attendu : un tableau d'objets { \"question\", \"userId\", \"clearance\" }.");
        }

        var items = new List<QuestionSetItem>(entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];

            if (string.IsNullOrWhiteSpace(entry.Question))
            {
                throw new InvalidDataException(
                    $"Entree n°{i + 1} du fichier « {path} » : le champ « question » est vide.");
            }

            items.Add(new QuestionSetItem(
                entry.Question,
                string.IsNullOrWhiteSpace(entry.UserId) ? "anonymous" : entry.UserId,
                string.IsNullOrWhiteSpace(entry.Clearance) ? "public" : entry.Clearance));
        }

        return items;
    }

    /// <summary>Le chemin effectif du jeu de questions, tel qu'on veut pouvoir l'afficher.</summary>
    public static string ResolvePath(AssistantOptions options, string? requestedPath)
    {
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            return Path.Combine(PathResolver.Resolve(options.SnapshotsDirectory), DefaultFileName);
        }

        var trimmed = requestedPath.Trim();

        // Un chemin relatif au repertoire courant est ce que tape un humain ; s'il ne
        // designe rien, on retombe sur la resolution depuis la racine du depot.
        return File.Exists(trimmed) ? Path.GetFullPath(trimmed) : PathResolver.Resolve(trimmed);
    }

    /// <summary>Forme brute d'une entree du fichier. Les champs supplementaires sont ignores.</summary>
    private sealed class QuestionEntry
    {
        public string? Question { get; set; }

        public string? UserId { get; set; }

        public string? Clearance { get; set; }
    }
}
