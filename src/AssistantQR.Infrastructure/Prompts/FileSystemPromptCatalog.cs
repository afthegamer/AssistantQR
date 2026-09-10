using System.Text;

using AssistantQR.Application.Model;
using AssistantQR.Application.Ports;

namespace AssistantQR.Infrastructure.Prompts;

/// <summary>
/// Adaptateur REEL de <see cref="IPromptCatalog"/> : les gabarits sont des fichiers
/// <c>&lt;nom&gt;@&lt;version&gt;.md</c> a en-tete YAML, ranges dans un dossier.
/// </summary>
/// <remarks>
/// LA CONVENTION DE NOMMAGE EST LE COEUR DE L'ADAPTATEUR. Mettre la version dans le NOM
/// du fichier, et non dans son contenu ni dans l'historique Git, a une consequence
/// pratique : deux versions d'un prompt coexistent sur le disque. On peut donc rejouer
/// le meme jeu de questions sous 1.0.0 puis sous 1.1.0 et comparer, ce qu'un fichier
/// unique qu'on modifie en place rendrait impossible — l'ancienne version aurait
/// disparu au moment ou l'on en aurait eu besoin.
///
/// LE CHARGEMENT EST INTEGRAL ET IMMEDIAT. Le port est synchrone et le catalogue lit
/// tout dans son constructeur : un gabarit absent ou mal forme est une erreur de
/// configuration, elle doit interrompre le demarrage, pas surgir a la premiere question
/// d'un utilisateur. La verification que l'en-tete concorde avec le nom du fichier va
/// dans le meme sens : un prompt qui se declare 1.0.0 dans un fichier nomme 1.1.0
/// rendrait toute empreinte de configuration mensongere.
/// </remarks>
public sealed class FileSystemPromptCatalog : IPromptCatalog
{
    private const string Delimiter = "---";

    private readonly Dictionary<string, PromptTemplate> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Entry> _entries = new();
    private readonly List<PromptDescriptor> _descriptors = new();

    /// <summary>Charge tous les gabarits du dossier.</summary>
    /// <exception cref="DirectoryNotFoundException">Si le dossier n'existe pas.</exception>
    /// <exception cref="InvalidDataException">Si un fichier est mal forme, ou si le dossier ne contient aucun gabarit.</exception>
    public FileSystemPromptCatalog(string promptsDirectory)
    {
        if (string.IsNullOrWhiteSpace(promptsDirectory))
        {
            throw new ArgumentException("Le dossier des gabarits doit être renseigné.", nameof(promptsDirectory));
        }

        if (!Directory.Exists(promptsDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Le dossier des gabarits de prompt est introuvable : « {ToDisplayPath(promptsDirectory)} ». " +
                "Vérifiez le paramètre « PromptsDirectory » de la configuration.");
        }

        var files = Directory.GetFiles(promptsDirectory, "*.md", SearchOption.TopDirectoryOnly);
        Array.Sort(files, StringComparer.Ordinal);

        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);
            var stem = Path.GetFileNameWithoutExtension(file);

            // Un fichier sans « @ » n'est pas un gabarit : c'est le README qui explique
            // la convention. On l'ignore plutot que de faire echouer le chargement.
            var separator = stem.LastIndexOf('@');
            if (separator <= 0 || separator == stem.Length - 1)
            {
                continue;
            }

            var template = ReadTemplate(file, fileName, stem[..separator], stem[(separator + 1)..]);
            var key = BuildKey(template.Name, template.Version);

            if (!_byKey.TryAdd(key, template))
            {
                throw new InvalidDataException(
                    $"Le gabarit « {key} » est déclaré par deux fichiers différents.");
            }

            _entries.Add(new Entry(template, ParseVersion(fileName, template.Version)));
            _descriptors.Add(template.Describe());
        }

        if (_entries.Count == 0)
        {
            throw new InvalidDataException(
                $"Aucun gabarit trouvé dans « {ToDisplayPath(promptsDirectory)} ». " +
                "Les fichiers doivent être nommés « nom@version.md ».");
        }
    }

    /// <inheritdoc />
    public PromptTemplate Get(string name, string version)
    {
        if (_byKey.TryGetValue(BuildKey(name, version), out var template))
        {
            return template;
        }

        throw new InvalidOperationException(
            $"Le gabarit « {BuildKey(name, version)} » est absent du catalogue. " +
            $"Disponibles : {DescribeAvailable()}.");
    }

    /// <inheritdoc />
    public PromptTemplate GetLatest(string name)
    {
        Entry? best = null;

        foreach (var entry in _entries)
        {
            if (!string.Equals(entry.Template.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (best is null || entry.Semantic > best.Semantic)
            {
                best = entry;
            }
        }

        if (best is null)
        {
            throw new InvalidOperationException(
                $"Aucun gabarit ne porte le nom « {name} ». Disponibles : {DescribeAvailable()}.");
        }

        return best.Template;
    }

    /// <inheritdoc />
    public IReadOnlyList<PromptDescriptor> List() => _descriptors;

    private static string BuildKey(string name, string version) => $"{name?.Trim()}@{version?.Trim()}";

    private string DescribeAvailable() =>
        _descriptors.Count == 0
            ? "(catalogue vide)"
            : string.Join(", ", _descriptors.Select(d => $"{d.Name}@{d.Version}"));

    private static PromptTemplate ReadTemplate(string path, string fileName, string fileStemName, string fileStemVersion)
    {
        var rawText = File.ReadAllText(path, Encoding.UTF8);
        var lines = SplitLines(rawText);

        var index = 0;
        while (index < lines.Length && lines[index].Trim().Length == 0)
        {
            index++;
        }

        if (index >= lines.Length || lines[index].Trim() != Delimiter)
        {
            throw new InvalidDataException(
                $"Le gabarit « {fileName} » ne commence pas par un en-tête « --- ».");
        }

        index++;

        var scalars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var placeholders = new List<string>();
        string? lastKey = null;
        var headerClosed = false;

        for (; index < lines.Length; index++)
        {
            var line = lines[index];
            var trimmed = line.Trim();

            if (trimmed == Delimiter)
            {
                headerClosed = true;
                index++;
                break;
            }

            if (lastKey is not null && trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                if (string.Equals(lastKey, "placeholders", StringComparison.OrdinalIgnoreCase))
                {
                    AddPlaceholder(placeholders, trimmed[2..]);
                }

                continue;
            }

            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();

            lastKey = key;
            scalars[key] = Unquote(value);

            if (string.Equals(key, "placeholders", StringComparison.OrdinalIgnoreCase) &&
                value.Length >= 2 && value[0] == '[' && value[^1] == ']')
            {
                foreach (var item in value[1..^1].Split(','))
                {
                    AddPlaceholder(placeholders, item);
                }
            }
        }

        if (!headerClosed)
        {
            throw new InvalidDataException(
                $"L'en-tête du gabarit « {fileName} » n'est jamais refermé par « --- ».");
        }

        var name = scalars.GetValueOrDefault("name", string.Empty);
        var version = scalars.GetValueOrDefault("version", string.Empty);

        if (name.Length == 0 || version.Length == 0)
        {
            throw new InvalidDataException(
                $"Le gabarit « {fileName} » doit déclarer « name » et « version » dans son en-tête.");
        }

        if (!string.Equals(name, fileStemName, StringComparison.Ordinal) ||
            !string.Equals(version, fileStemVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Le gabarit « {fileName} » se déclare « {name}@{version} », " +
                $"ce qui ne correspond pas à son nom de fichier.");
        }

        var body = string.Join('\n', lines[index..]).Trim();
        if (body.Length == 0)
        {
            throw new InvalidDataException($"Le gabarit « {fileName} » n'a pas de corps sous son en-tête.");
        }

        return new PromptTemplate(name, version, body, placeholders);
    }

    private static Version ParseVersion(string fileName, string version)
    {
        // La comparaison est semantique et non alphabetique : « 1.10.0 » vient APRES
        // « 1.9.0 », ce qu'un tri de chaines aurait inverse.
        if (!Version.TryParse(version, out var parsed))
        {
            throw new InvalidDataException(
                $"La version « {version} » du gabarit « {fileName} » n'est pas au format majeur.mineur.correctif.");
        }

        return parsed;
    }

    private static void AddPlaceholder(List<string> placeholders, string raw)
    {
        var value = Unquote(raw);
        if (value.Length > 0 && !placeholders.Contains(value, StringComparer.Ordinal))
        {
            placeholders.Add(value);
        }
    }

    private static string[] SplitLines(string rawText)
    {
        var text = rawText;

        if (text.Length > 0 && text[0] == ByteOrderMark)
        {
            text = text[1..];
        }

        return text.Replace("\r\n", "\n", StringComparison.Ordinal)
                   .Replace('\r', '\n')
                   .Split('\n');
    }

    private static string Unquote(string value)
    {
        var trimmed = value.Trim();

        if (trimmed.Length >= 2 &&
            ((trimmed[0] == '"' && trimmed[^1] == '"') || (trimmed[0] == '\'' && trimmed[^1] == '\'')))
        {
            trimmed = trimmed[1..^1].Trim();
        }

        return trimmed;
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

    /// <summary>Marque d'ordre des octets, que certains editeurs Windows ajoutent en tete de fichier.</summary>
    private const char ByteOrderMark = (char)0xFEFF;

    /// <summary>Un gabarit et sa version analysee, pour que <c>GetLatest</c> compare des nombres.</summary>
    private sealed record Entry(PromptTemplate Template, Version Semantic);
}
