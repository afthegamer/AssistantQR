using System.Text;

using AssistantQR.Application.Model;
using AssistantQR.Application.Ports;
using AssistantQR.Domain.Documents;

namespace AssistantQR.Infrastructure.Chunking;

/// <summary>
/// Adaptateur REEL de <see cref="IChunkingStrategy"/> : decoupage sur les paragraphes.
/// </summary>
/// <remarks>
/// C'est la strategie par defaut parce qu'elle respecte la structure que l'auteur du
/// document a lui-meme choisie. Un paragraphe est deja une unite de sens : le decoupage
/// n'invente pas de frontiere, il reprend celles qui existent.
///
/// LA FUSION DES PARAGRAPHES COURTS N'EST PAS UN DETAIL. Un morceau de trente
/// caracteres (« Tarif réduit : 4 €. ») produit un vecteur domine par deux ou trois
/// mots. Il remonte tres haut sur une requete qui contient ces mots et tres bas
/// partout ailleurs, sans jamais contenir assez de contexte pour appuyer une reponse.
/// Fusionner ces bribes avec le paragraphe suivant vaut mieux que les indexer seules.
/// Ce reglage — <c>minChars</c> — change les reponses du systeme sans qu'aucune regle
/// metier ne bouge : c'est exactement le principe CACE a l'oeuvre.
/// </remarks>
public sealed class ParagraphChunkingStrategy : IChunkingStrategy
{
    private readonly int _minChars;

    /// <summary>Construit la strategie.</summary>
    /// <param name="minChars">Taille en deca de laquelle un bloc est fusionne avec le suivant.</param>
    public ParagraphChunkingStrategy(int minChars = 120)
    {
        if (minChars < 0)
        {
            throw new ArgumentException(
                $"La taille minimale d'un morceau ne peut pas être négative : {minChars}.", nameof(minChars));
        }

        _minChars = minChars;
    }

    /// <inheritdoc />
    public string Id => "paragraph";

    /// <inheritdoc />
    public string Description =>
        $"Découpage sur les lignes vides ; les blocs de moins de {_minChars} caractères sont fusionnés avec le suivant.";

    /// <inheritdoc />
    public IReadOnlyList<Chunk> Split(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var blocks = ExtractBlocks(document.Content);
        var merged = Merge(blocks);

        // Filet de securite : un document entierement compose de titres ne doit pas
        // disparaitre silencieusement de l'index. Mieux vaut un morceau imparfait
        // qu'un document introuvable — un trou dans l'index se diagnostique tres mal.
        if (merged.Count == 0)
        {
            merged.Add(document.Content.Trim());
        }

        var chunks = new List<Chunk>(merged.Count);
        for (var ordinal = 0; ordinal < merged.Count; ordinal++)
        {
            chunks.Add(new Chunk(
                Chunk.BuildId(document.Id, ordinal),
                document.Id,
                document.Title,
                document.AccessLevel,
                ordinal,
                merged[ordinal]));
        }

        return chunks;
    }

    private static List<string> ExtractBlocks(string content)
    {
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal)
                           .Replace('\r', '\n')
                           .Split('\n');

        var blocks = new List<string>();
        var current = new List<string>();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            if (trimmed.Length == 0)
            {
                Flush(blocks, current);
                continue;
            }

            current.Add(trimmed);
        }

        Flush(blocks, current);

        return blocks;
    }

    private static void Flush(List<string> blocks, List<string> current)
    {
        if (current.Count == 0)
        {
            return;
        }

        // Un bloc reduit a des titres markdown (« ## Tarifs ») n'apporte aucun contenu
        // citable : on le laisse tomber. En revanche un titre SUIVI de son texte, sans
        // ligne vide entre les deux, est conserve entier — le titre y sert de contexte.
        var hasProse = false;
        foreach (var line in current)
        {
            if (!IsHeading(line))
            {
                hasProse = true;
                break;
            }
        }

        if (hasProse)
        {
            blocks.Add(string.Join('\n', current));
        }

        current.Clear();
    }

    private static bool IsHeading(string line)
    {
        var hashes = 0;
        while (hashes < line.Length && line[hashes] == '#')
        {
            hashes++;
        }

        return hashes is > 0 and <= 6 && hashes < line.Length && char.IsWhiteSpace(line[hashes]);
    }

    private List<string> Merge(List<string> blocks)
    {
        var merged = new List<string>();
        var buffer = new StringBuilder();

        foreach (var block in blocks)
        {
            if (buffer.Length > 0)
            {
                buffer.Append("\n\n");
            }

            buffer.Append(block);

            if (buffer.Length >= _minChars)
            {
                merged.Add(buffer.ToString());
                buffer.Clear();
            }
        }

        if (buffer.Length > 0)
        {
            // Le dernier bloc n'a pas de suivant avec qui fusionner. On le rattache au
            // precedent plutot que d'emettre un morceau trop court : la regle vise a
            // ce qu'aucun morceau indexe ne soit famelique, pas a suivre la lettre.
            if (merged.Count > 0)
            {
                merged[^1] = merged[^1] + "\n\n" + buffer.ToString();
            }
            else
            {
                merged.Add(buffer.ToString());
            }
        }

        return merged;
    }
}
