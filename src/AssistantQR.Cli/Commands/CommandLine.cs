using System.Globalization;

namespace AssistantQR.Cli.Commands;

/// <summary>
/// Le dispatcher maison : des mots-cles positionnels, des options « --nom valeur », rien
/// d'autre.
/// </summary>
/// <remarks>
/// POURQUOI PAS DE BIBLIOTHEQUE D'ANALYSE D'ARGUMENTS. Le depot en aurait le droit, et
/// dans un vrai projet ce serait le choix raisonnable. Ici, l'enjeu du cours est que la
/// couche de presentation reste MANIFESTEMENT MINCE : on doit pouvoir la lire en entier
/// et constater qu'elle ne decide rien. Une bibliotheque de plusieurs milliers de lignes,
/// avec ses conventions, ses attributs et son modele d'hebergement, deplacerait
/// l'attention vers elle. Cent lignes de decoupage de chaines se lisent d'un trait et
/// laissent la place aux ports.
///
/// LE PARSEUR NE VALIDE RIEN D'AUTRE QUE LA SYNTAXE. Qu'un niveau d'habilitation soit
/// connu, qu'un topK soit positif, qu'une strategie de decoupage existe : ce sont des
/// questions pour le Domain, pour <c>PipelineOptions.Validate</c> ou pour une fabrique
/// d'infrastructure. La presentation se contente de transformer du texte en valeurs et
/// de refuser ce qui n'a pas la forme d'une valeur.
/// </remarks>
internal sealed class CommandLine
{
    private readonly List<string> _positionals = new();
    private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);

    private CommandLine()
    {
    }

    /// <summary>Les mots-cles, dans l'ordre : « snapshot », « compare », « base », « candidat ».</summary>
    public IReadOnlyList<string> Positionals => _positionals;

    /// <summary>Les noms d'options rencontres, sans les deux tirets.</summary>
    public IReadOnlyCollection<string> OptionNames => _options.Keys;

    /// <summary>Decoupe la ligne de commande. Accepte « --nom valeur » et « --nom=valeur ».</summary>
    public static CommandLine Parse(IReadOnlyList<string> arguments)
    {
        var line = new CommandLine();

        for (var i = 0; i < arguments.Count; i++)
        {
            var token = arguments[i] ?? string.Empty;

            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                line._positionals.Add(token);
                continue;
            }

            var name = token[2..];
            string? value = null;

            var separator = name.IndexOf('=', StringComparison.Ordinal);
            if (separator >= 0)
            {
                value = name[(separator + 1)..];
                name = name[..separator];
            }
            else if (i + 1 < arguments.Count && !arguments[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                // Une valeur negative (« --seed -1 ») ne commence pas par deux tirets :
                // elle est donc bien consommee comme valeur, pas comme option suivante.
                value = arguments[++i];
            }

            if (name.Length == 0)
            {
                throw new UsageException("« -- » seul n'est pas un nom d'option.");
            }

            line._options[name] = value;
        }

        return line;
    }

    /// <summary>Le mot-cle a la position demandee, ou <c>null</c>.</summary>
    public string? Positional(int index) =>
        index >= 0 && index < _positionals.Count ? _positionals[index] : null;

    /// <summary>L'option est-elle presente, avec ou sans valeur ?</summary>
    public bool Has(string name) => _options.ContainsKey(name);

    /// <summary>La valeur d'une option, ou <c>null</c> si elle est absente ou sans valeur.</summary>
    public string? Value(string name) => _options.TryGetValue(name, out var value) ? value : null;

    /// <summary>La valeur d'une option presente, en refusant la forme « drapeau ».</summary>
    public string RequiredValue(string name) =>
        Value(name) ?? throw new UsageException($"L'option « --{name} » attend une valeur.");

    /// <summary>Un entier, ou <c>null</c> si l'option est absente.</summary>
    public int? Integer(string name)
    {
        if (!Has(name))
        {
            return null;
        }

        var raw = RequiredValue(name);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new UsageException($"L'option « --{name} » attend un entier, or elle a recu « {raw} ».");
    }

    /// <summary>Un nombre decimal (point decimal), ou <c>null</c> si l'option est absente.</summary>
    public double? Number(string name)
    {
        if (!Has(name))
        {
            return null;
        }

        var raw = RequiredValue(name);
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new UsageException(
                $"L'option « --{name} » attend un nombre decimal (point decimal), or elle a recu « {raw} ».");
    }

    /// <summary>Une paire « a,b », utilisee par les demonstrations qui comparent deux reglages.</summary>
    public (string First, string Second)? Pair(string name)
    {
        if (!Has(name))
        {
            return null;
        }

        var raw = RequiredValue(name);
        var parts = raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        return parts.Length == 2
            ? (parts[0], parts[1])
            : throw new UsageException(
                $"L'option « --{name} » attend deux valeurs separees par une virgule, " +
                $"par exemple « --{name} a,b ». Recu : « {raw} ».");
    }

    /// <summary>
    /// Refuse les options que la commande ne connait pas. Une faute de frappe silencieuse
    /// (« --clearence ») produirait une execution avec les valeurs par defaut et un
    /// resultat qu'on attribuerait au systeme plutot qu'a la commande tapee.
    /// </summary>
    public void EnsureKnownOptions(params string[] accepted)
    {
        foreach (var name in _options.Keys)
        {
            if (accepted.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var list = accepted.Length == 0
                ? "(aucune)"
                : string.Join(", ", accepted.Select(option => "--" + option));

            throw new UsageException($"Option inconnue : « --{name} ». Options acceptees ici : {list}.");
        }
    }
}
