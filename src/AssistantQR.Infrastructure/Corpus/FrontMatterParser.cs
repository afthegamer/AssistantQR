using AssistantQR.Domain;
using AssistantQR.Domain.Access;
using AssistantQR.Domain.Documents;

namespace AssistantQR.Infrastructure.Corpus;

/// <summary>
/// Lecture de l'en-tete YAML minimal des fichiers du corpus.
/// </summary>
/// <remarks>
/// CE TYPE EST L'EXEMPLE CANONIQUE DE LA TRADUCTION A LA FRONTIERE.
/// Le corpus est redige en francais par des bibliothecaires : il porte des cles
/// francaises (<c>titre</c>, <c>niveau</c>) et des valeurs francaises
/// (« interne », « confidentiel »). Le Domain, lui, ne connait que
/// <see cref="AccessLevel"/> et ses noms canoniques anglais.
///
/// La question n'est donc pas « faut-il traduire », mais « OU traduire ». Trois
/// reponses etaient possibles :
/// 1. Ecrire le corpus en anglais — on aurait deplace le probleme sur l'auteur du
///    corpus, qui n'est pas informaticien, pour le confort du programmeur.
/// 2. Faire accepter « interne » a <c>AccessLevel.Parse</c> — le Domain se serait mis
///    a connaitre la langue de l'un de ses fournisseurs de donnees. Le jour ou un
///    second corpus arrive en allemand, la regle metier se remplit de vocabulaire.
/// 3. Traduire ici, dans l'adaptateur — c'est le choix retenu. Le dialecte du monde
///    exterieur s'arrete a cette classe ; au-dela, il n'existe plus qu'un rang.
///
/// C'est aussi pour cela que ce type est <c>internal</c> : ce n'est pas un service
/// offert au reste du systeme, c'est la mecanique interne d'un adaptateur.
/// </remarks>
internal static class FrontMatterParser
{
    private const string Delimiter = "---";

    /// <summary>Marque d'ordre des octets, que certains editeurs Windows ajoutent en tete de fichier.</summary>
    private const char ByteOrderMark = (char)0xFEFF;

    /// <summary>
    /// Analyse un fichier du corpus, ou echoue en nommant le fichier fautif.
    /// </summary>
    /// <param name="fileName">Nom du fichier, extension comprise. Il sert de message d'erreur ET d'identifiant de repli.</param>
    /// <param name="rawText">Contenu integral du fichier.</param>
    /// <exception cref="InvalidDataException">Si l'en-tete est absent, incomplet ou incoherent.</exception>
    public static Document Parse(string fileName, string rawText)
    {
        var (document, error) = TryParse(fileName, rawText);

        if (document is null)
        {
            throw new InvalidDataException(error ?? $"Le fichier « {fileName} » n'a pas pu être analysé.");
        }

        return document;
    }

    /// <summary>
    /// Variante non levante : rend le document, ou l'explication de l'echec.
    /// Exactement l'un des deux membres du couple est non nul.
    /// </summary>
    public static (Document? Document, string? Error) TryParse(string fileName, string rawText)
    {
        var lines = SplitLines(rawText);

        var index = 0;
        while (index < lines.Length && lines[index].Trim().Length == 0)
        {
            index++;
        }

        if (index >= lines.Length || lines[index].Trim() != Delimiter)
        {
            return (null, $"Le fichier « {fileName} » ne commence pas par un en-tête « --- ».");
        }

        index++;

        var scalars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lists = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
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

            // Element de liste sur sa propre ligne (« - accueil ») : il complete la
            // derniere cle rencontree. Tolerance utile, le corpus utilisant surtout
            // la forme compacte « tags: [a, b] ».
            if (lastKey is not null && trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                lists[lastKey].Add(Unquote(trimmed[2..]));
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
            lists[key] = ParseInlineList(value);
        }

        if (!headerClosed)
        {
            return (null, $"L'en-tête du fichier « {fileName} » n'est jamais refermé par « --- ».");
        }

        var body = string.Join('\n', lines[index..]).Trim();
        if (body.Length == 0)
        {
            return (null, $"Le fichier « {fileName} » n'a pas de contenu sous son en-tête.");
        }

        var declaredId = scalars.GetValueOrDefault("id", string.Empty);
        var fileStem = Path.GetFileNameWithoutExtension(fileName);
        var id = declaredId.Length == 0 ? fileStem : declaredId;

        // L'identifiant voyage jusque dans les citations en ligne « [identifiant] »
        // imposees au modele. Le laisser diverger du nom de fichier rendrait toute
        // verification manuelle d'une reponse penible : on refuse tot.
        if (!string.Equals(id, fileStem, StringComparison.Ordinal))
        {
            return (null, $"Le fichier « {fileName} » déclare l'identifiant « {id} », qui ne correspond pas à son nom.");
        }

        if (!scalars.TryGetValue("titre", out var title) || title.Trim().Length == 0)
        {
            return (null, $"Le fichier « {fileName} » ne déclare pas de « titre ».");
        }

        if (!scalars.TryGetValue("niveau", out var level))
        {
            return (null, $"Le fichier « {fileName} » ne déclare pas de « niveau ».");
        }

        if (!TryTranslateAccessLevel(level, out var accessLevel))
        {
            return (null,
                $"Le fichier « {fileName} » déclare un niveau inconnu : « {level} ». " +
                "Attendu : public, interne ou confidentiel.");
        }

        var tags = lists.GetValueOrDefault("tags") ?? new List<string>();

        try
        {
            return (new Document(DocumentId.From(id), title, body, accessLevel, tags), null);
        }
        catch (DomainException exception)
        {
            // Une invariante du Domain violee par une donnee exterieure n'est pas un
            // bogue metier : c'est un fichier mal ecrit. On la retraduit en erreur de
            // lecture, en nommant le fichier — seul renseignement exploitable ici.
            return (null, $"Le fichier « {fileName} » est invalide : {exception.Message}");
        }
    }

    /// <summary>
    /// Traduit le niveau d'acces francais du corpus vers le vocabulaire du Domain.
    /// Les formes anglaises sont acceptees par commodite pour les jeux de test.
    /// </summary>
    public static bool TryTranslateAccessLevel(string? french, out AccessLevel level)
    {
        switch (french?.Trim().ToLowerInvariant())
        {
            case "public":
                level = AccessLevel.Public;
                return true;
            case "interne":
            case "internal":
                level = AccessLevel.Internal;
                return true;
            case "confidentiel":
            case "confidentielle":
            case "confidential":
                level = AccessLevel.Confidential;
                return true;
            default:
                level = default;
                return false;
        }
    }

    private static string[] SplitLines(string? rawText)
    {
        var text = rawText ?? string.Empty;

        if (text.Length > 0 && text[0] == ByteOrderMark)
        {
            text = text[1..];
        }

        return text.Replace("\r\n", "\n", StringComparison.Ordinal)
                   .Replace('\r', '\n')
                   .Split('\n');
    }

    private static List<string> ParseInlineList(string value)
    {
        var items = new List<string>();

        if (value.Length < 2 || value[0] != '[' || value[^1] != ']')
        {
            return items;
        }

        foreach (var part in value[1..^1].Split(','))
        {
            var item = Unquote(part);
            if (item.Length > 0)
            {
                items.Add(item);
            }
        }

        return items;
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
}
