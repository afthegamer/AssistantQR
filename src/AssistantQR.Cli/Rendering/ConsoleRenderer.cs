using System.Globalization;
using System.Text;

namespace AssistantQR.Cli.Rendering;

/// <summary>
/// Tout ce que le programme sait de son terminal : une largeur, deux flux, et des
/// colonnes alignees.
/// </summary>
/// <remarks>
/// POURQUOI UN TYPE PLUTOT QUE DES APPELS A <c>Console.WriteLine</c> DISSEMINES.
/// La presentation est la couche ou l'on est le plus tente de melanger les genres :
/// une commande qui calcule ET met en forme finit par ne plus etre lisible, et ses
/// choix d'affichage se dupliquent d'une commande a l'autre. Concentrer la mise en
/// forme ici laisse aux commandes un role qu'on peut enoncer en une phrase : traduire
/// des arguments en commandes de cas d'usage, puis passer les resultats a ce rendu.
///
/// AUCUNE COULEUR, AUCUN CARACTERE HORS ASCII DANS LES ARMATURES. Le terminal par
/// defaut de Windows n'interprete pas toujours les sequences ANSI et n'affiche pas
/// tous les glyphes ; un tableau qui se decompose est un tableau qu'on ne lit pas.
/// Les accents, eux, sont indispensables — le texte est en francais — d'ou l'encodage
/// UTF-8 force au demarrage du programme.
/// </remarks>
internal sealed class ConsoleRenderer
{
    private const int MinimumWidth = 72;
    private const int MaximumWidth = 120;
    private const int DefaultWidth = 100;

    /// <summary>Au-dela, une colonne de tete mange la place des phrases.</summary>
    private const int MaximumLeadingColumn = 40;

    /// <summary>En deca, une colonne ne montre plus rien d'utile.</summary>
    private const int MinimumLeadingColumn = 12;

    /// <summary>Largeur en deca de laquelle la derniere colonne coupe les mots en deux.</summary>
    private const int MinimumLastColumn = 30;

    private readonly TextWriter _output;
    private readonly TextWriter _error;

    /// <summary>Construit un rendu vers des flux donnes, avec une largeur bornee.</summary>
    public ConsoleRenderer(TextWriter output, TextWriter error, int width)
    {
        _output = output;
        _error = error;
        Width = Math.Clamp(width, MinimumWidth, MaximumWidth);
    }

    /// <summary>Largeur utile, en caracteres.</summary>
    public int Width { get; }

    /// <summary>
    /// Rendu branche sur la console reelle. La largeur est lue si elle est lisible :
    /// une sortie redirigee (fichier, tube) fait lever <c>WindowWidth</c>, auquel cas on
    /// retombe sur une largeur fixe plutot que d'echouer.
    /// </summary>
    public static ConsoleRenderer ForConsole()
    {
        var width = DefaultWidth;

        try
        {
            if (!Console.IsOutputRedirected && Console.WindowWidth > 0)
            {
                width = Console.WindowWidth - 1;
            }
        }
        catch (IOException)
        {
            // Terminal indisponible : la largeur par defaut fera l'affaire.
        }

        return new ConsoleRenderer(Console.Out, Console.Error, width);
    }

    /// <summary>Une ligne vide.</summary>
    public void Line() => _output.WriteLine();

    /// <summary>Une ligne telle quelle, sans repli automatique.</summary>
    public void Line(string text) => _output.WriteLine(text);

    /// <summary>Titre de commande : ligne vide, texte, soulignement plein.</summary>
    public void Title(string text)
    {
        Line();
        _output.WriteLine(text);
        _output.WriteLine(new string('=', Math.Min(Width, text.Length)));
    }

    /// <summary>Sous-titre : ligne vide, texte, soulignement leger.</summary>
    public void Section(string text)
    {
        Line();
        _output.WriteLine(text);
        _output.WriteLine(new string('-', Math.Min(Width, text.Length)));
    }

    /// <summary>Un paragraphe replie a la largeur du terminal.</summary>
    public void Paragraph(string text)
    {
        foreach (var line in Wrap(text, Width))
        {
            _output.WriteLine(line);
        }
    }

    /// <summary>Un paragraphe indente, introduit par un tiret.</summary>
    public void Bullet(string text)
    {
        var lines = Wrap(text, Width - 4);
        for (var i = 0; i < lines.Count; i++)
        {
            _output.WriteLine(i == 0 ? "  - " + lines[i] : "    " + lines[i]);
        }
    }

    /// <summary>Une remarque de deroulement, indentee pour ne pas se confondre avec un resultat.</summary>
    public void Note(string text)
    {
        foreach (var line in Wrap(text, Width - 2))
        {
            _output.WriteLine("  " + line);
        }
    }

    /// <summary>Un message d'echec, sur la sortie d'erreur.</summary>
    public void Failure(string text)
    {
        foreach (var line in Wrap(text, Width))
        {
            _error.WriteLine(line);
        }
    }

    /// <summary>Une liste « etiquette : valeur » dont les deux-points sont alignes.</summary>
    public void Pairs(IReadOnlyList<(string Label, string Value)> pairs)
    {
        if (pairs.Count == 0)
        {
            return;
        }

        var labelWidth = 0;
        foreach (var pair in pairs)
        {
            labelWidth = Math.Max(labelWidth, pair.Label.Length);
        }

        labelWidth = Math.Min(labelWidth, 32);
        var valueWidth = Math.Max(20, Width - labelWidth - 3);

        foreach (var (label, value) in pairs)
        {
            var lines = Wrap(value, valueWidth);
            for (var i = 0; i < lines.Count; i++)
            {
                var prefix = i == 0
                    ? Fit(label, labelWidth) + " : "
                    : new string(' ', labelWidth + 3);

                _output.WriteLine((prefix + lines[i]).TrimEnd());
            }
        }
    }

    /// <summary>
    /// Un tableau a colonnes alignees. La derniere colonne absorbe la largeur restante et
    /// se replie sur plusieurs lignes : les messages en francais sont longs, et un message
    /// tronque cesse d'etre actionnable.
    /// </summary>
    public void Table(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var count = headers.Count;
        var widths = new int[count];

        for (var i = 0; i < count; i++)
        {
            widths[i] = headers[i].Length;
        }

        foreach (var row in rows)
        {
            for (var i = 0; i < count && i < row.Count; i++)
            {
                widths[i] = Math.Max(widths[i], (row[i] ?? string.Empty).Length);
            }
        }

        var last = count - 1;
        var naturalLast = widths[last];

        var fixedWidth = 0;
        for (var i = 0; i < last; i++)
        {
            widths[i] = Math.Min(widths[i], MaximumLeadingColumn);
            fixedWidth += widths[i] + 2;
        }

        // La derniere colonne porte les phrases ; si les precedentes l'etranglent, les mots
        // y sont coupes en deux et le tableau devient illisible. On rogne donc la plus
        // large des colonnes de tete jusqu'a lui rendre de quoi respirer.
        while (Width - fixedWidth < MinimumLastColumn)
        {
            var widest = -1;
            for (var i = 0; i < last; i++)
            {
                if (widths[i] > MinimumLeadingColumn && (widest < 0 || widths[i] > widths[widest]))
                {
                    widest = i;
                }
            }

            if (widest < 0)
            {
                break;
            }

            widths[widest]--;
            fixedWidth--;
        }

        widths[last] = Math.Max(MinimumLeadingColumn, Math.Min(naturalLast, Width - fixedWidth));

        var header = new StringBuilder();
        var underline = new StringBuilder();

        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                header.Append("  ");
                underline.Append("  ");
            }

            header.Append(Fit(headers[i], widths[i]));
            underline.Append('-', widths[i]);
        }

        _output.WriteLine(header.ToString().TrimEnd());
        _output.WriteLine(underline.ToString());

        foreach (var row in rows)
        {
            WriteRow(row, widths, fixedWidth);
        }
    }

    /// <summary>
    /// Deux colonnes face a face. C'est la seule mise en forme qui rende une comparaison
    /// lisible sans que l'oeil ait a memoriser la premiere moitie de la page.
    /// </summary>
    public void SideBySide(string leftTitle, string rightTitle, IReadOnlyList<(string Left, string Right)> rows)
    {
        var column = Math.Max(24, (Width - 3) / 2);

        _output.WriteLine((Fit(leftTitle, column) + " | " + rightTitle).TrimEnd());
        _output.WriteLine(new string('-', column) + "-+-" + new string('-', column));

        foreach (var (left, right) in rows)
        {
            var leftLines = Wrap(left, column);
            var rightLines = Wrap(right, column);
            var height = Math.Max(leftLines.Count, rightLines.Count);

            for (var i = 0; i < height; i++)
            {
                var leftText = i < leftLines.Count ? leftLines[i] : string.Empty;
                var rightText = i < rightLines.Count ? rightLines[i] : string.Empty;
                _output.WriteLine((Fit(leftText, column) + " | " + rightText).TrimEnd());
            }

            _output.WriteLine();
        }
    }

    /// <summary>Complete a droite, ou tronque en signalant la coupe.</summary>
    public static string Fit(string? text, int width)
    {
        var value = text ?? string.Empty;

        if (value.Length == width)
        {
            return value;
        }

        return value.Length < width
            ? value.PadRight(width)
            : string.Concat(value.AsSpan(0, Math.Max(1, width - 3)), "...");
    }

    /// <summary>Decoupe un texte sur les frontieres de mot, en respectant les retours a la ligne.</summary>
    public static IReadOnlyList<string> Wrap(string? text, int width)
    {
        var limit = Math.Max(8, width);
        var lines = new List<string>();
        var source = (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal);

        foreach (var rawLine in source.Split('\n'))
        {
            var builder = new StringBuilder();

            foreach (var word in rawLine.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var piece = word;

                // Un identifiant plus long que la colonne doit etre coupe de force,
                // sinon il decale toute la mise en page.
                while (piece.Length > limit)
                {
                    if (builder.Length > 0)
                    {
                        lines.Add(builder.ToString());
                        builder.Clear();
                    }

                    lines.Add(piece[..limit]);
                    piece = piece[limit..];
                }

                if (builder.Length == 0)
                {
                    builder.Append(piece);
                }
                else if (builder.Length + 1 + piece.Length <= limit)
                {
                    builder.Append(' ').Append(piece);
                }
                else
                {
                    lines.Add(builder.ToString());
                    builder.Clear();
                    builder.Append(piece);
                }
            }

            lines.Add(builder.ToString());
        }

        if (lines.Count == 0)
        {
            lines.Add(string.Empty);
        }

        return lines;
    }

    /// <summary>Une duree lisible : millisecondes en dessous de la seconde, secondes au-dela.</summary>
    public static string Duration(TimeSpan duration) =>
        duration.TotalSeconds < 1
            ? duration.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + " ms"
            : duration.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture) + " s";

    /// <summary>Un score de similarite, toujours sur trois decimales pour rester comparable a l'oeil.</summary>
    public static string Score(double score) => score.ToString("0.000", CultureInfo.InvariantCulture);

    /// <summary>Une proportion exprimee en pourcentage.</summary>
    public static string Percent(double ratio) =>
        (ratio * 100).ToString("0.0", CultureInfo.InvariantCulture) + " %";

    private void WriteRow(IReadOnlyList<string> row, IReadOnlyList<int> widths, int fixedWidth)
    {
        var last = widths.Count - 1;
        var lastValue = last < row.Count ? row[last] ?? string.Empty : string.Empty;
        var wrapped = Wrap(lastValue, widths[last]);

        for (var line = 0; line < wrapped.Count; line++)
        {
            var builder = new StringBuilder();

            if (line == 0)
            {
                for (var i = 0; i < last; i++)
                {
                    builder.Append(Fit(i < row.Count ? row[i] : string.Empty, widths[i])).Append("  ");
                }
            }
            else
            {
                builder.Append(' ', fixedWidth);
            }

            builder.Append(wrapped[line]);
            _output.WriteLine(builder.ToString().TrimEnd());
        }
    }
}
