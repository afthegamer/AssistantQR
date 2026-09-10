using AssistantQR.Application.Model;
using AssistantQR.Application.Ports;
using AssistantQR.Domain.Documents;

namespace AssistantQR.Infrastructure.Chunking;

/// <summary>
/// Adaptateur REEL de <see cref="IChunkingStrategy"/> : fenetre glissante de taille fixe.
/// </summary>
/// <remarks>
/// C'est la strategie la plus repandue dans les systemes de recherche augmentee, et la
/// plus indifferente au texte : elle ignore la structure du document et coupe tous les
/// <c>size</c> caracteres. Son interet ici est d'etre le CONTRE-EXEMPLE utile.
///
/// LE RECOUVREMENT EXISTE POUR REPARER CE QUE LA COUPE CASSE. Une fenetre fixe tombe
/// tot ou tard au milieu d'une phrase, et separe une affirmation de sa condition
/// (« ...sauf pour les abonnés de moins de 18 ans »). Reprendre les <c>overlap</c>
/// derniers caracteres au morceau suivant donne une chance a la phrase d'apparaitre
/// entiere quelque part. C'est un rustine, pas une solution : le vrai remede serait de
/// couper la ou le sens s'arrete, ce que fait la strategie par paragraphes.
///
/// L'identifiant embarque les deux reglages (« fixed-600-100 ») parce qu'il finit dans
/// les metadonnees de l'index et dans l'empreinte de configuration : deux index
/// construits avec des tailles differentes ne doivent surtout pas se ressembler.
/// </remarks>
public sealed class FixedSizeChunkingStrategy : IChunkingStrategy
{
    private readonly int _size;
    private readonly int _overlap;

    /// <summary>Construit la strategie.</summary>
    /// <param name="size">Taille visee d'un morceau, en caracteres.</param>
    /// <param name="overlap">Nombre de caracteres repris au morceau precedent.</param>
    /// <exception cref="ArgumentException">Si une valeur est nulle ou negative, ou si le recouvrement atteint la taille.</exception>
    public FixedSizeChunkingStrategy(int size = 600, int overlap = 100)
    {
        if (size <= 0)
        {
            throw new ArgumentException($"La taille d'un morceau doit être positive : {size}.", nameof(size));
        }

        if (overlap <= 0)
        {
            throw new ArgumentException($"Le recouvrement doit être positif : {overlap}.", nameof(overlap));
        }

        if (overlap >= size)
        {
            // Un recouvrement superieur ou egal a la taille ne fait pas avancer la
            // fenetre : le decoupage ne terminerait jamais. On refuse a la construction.
            throw new ArgumentException(
                $"Le recouvrement ({overlap}) doit être strictement inférieur à la taille ({size}).",
                nameof(overlap));
        }

        _size = size;
        _overlap = overlap;
    }

    /// <inheritdoc />
    public string Id => $"fixed-{_size}-{_overlap}";

    /// <inheritdoc />
    public string Description =>
        $"Fenêtre fixe de {_size} caractères avec {_overlap} caractères de recouvrement, coupée sur une frontière de mot.";

    /// <inheritdoc />
    public IReadOnlyList<Chunk> Split(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var text = document.Content;
        var chunks = new List<Chunk>();
        var start = 0;
        var ordinal = 0;

        while (start < text.Length)
        {
            var end = Math.Min(start + _size, text.Length);

            if (end < text.Length)
            {
                // On recule jusqu'au dernier blanc, sans jamais rendre le morceau plus
                // court que la moitie de la taille visee : sur un texte sans espace
                // (une URL, un tableau), mieux vaut une coupe brutale qu'un morceau vide.
                var floor = start + Math.Max(1, _size / 2);
                var boundary = end;

                while (boundary > floor && !char.IsWhiteSpace(text[boundary - 1]))
                {
                    boundary--;
                }

                if (boundary > floor)
                {
                    end = boundary;
                }
            }

            var piece = text[start..end].Trim();
            if (piece.Length > 0)
            {
                chunks.Add(new Chunk(
                    Chunk.BuildId(document.Id, ordinal),
                    document.Id,
                    document.Title,
                    document.AccessLevel,
                    ordinal,
                    piece));

                ordinal++;
            }

            if (end >= text.Length)
            {
                break;
            }

            // La progression d'au moins un caractere est garantie explicitement :
            // c'est la seule protection contre une boucle infinie si les reglages
            // et la longueur du texte conspirent.
            start = Math.Max(end - _overlap, start + 1);
        }

        return chunks;
    }
}
