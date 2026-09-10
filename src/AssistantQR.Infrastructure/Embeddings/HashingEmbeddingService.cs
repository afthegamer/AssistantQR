using System.Text;

using AssistantQR.Application.Model;
using AssistantQR.Application.Ports;

namespace AssistantQR.Infrastructure.Embeddings;

/// <summary>
/// Adaptateur FACTICE de <see cref="IEmbeddingService"/> : un sac de mots hache, sans
/// reseau, sans modele, sans etat.
/// </summary>
/// <remarks>
/// POURQUOI CE FAUX-LA ET PAS UN GENERATEUR ALEATOIRE : une doublure qui renvoie du
/// bruit rend les tests verts mais vides — on verifie que la tuyauterie ne casse pas,
/// jamais qu'elle transporte quelque chose. Ici, deux textes qui partagent des mots
/// partagent des coordonnees, donc le cosinus remonte. La recherche reste *significative*
/// hors ligne : « Quels sont les horaires du samedi ? » ramene bien le document sur les
/// horaires. C'est ce qui permet de faire tourner la demonstration complete, y compris
/// le scenario de fuite d'acces, sans installer Ollama.
///
/// CE QUE CE FAUX N'EST PAS : un modele semantique. Il ne connait ni synonymes, ni
/// paraphrases — « tarif » et « prix » sont deux dimensions etrangeres l'une a l'autre.
/// Le passage au vrai modele changera les reponses, et c'est precisement ce que les
/// instantanes servent a rendre visible.
///
/// Les bigrammes de mots (« salle de travail » contribue aussi via « salle de » et
/// « de travail ») donnent un peu de sensibilite a l'ordre des mots, ce qu'un pur sac
/// de mots ne fait pas du tout.
///
/// L'algorithme est fige par le contrat partage — minuscules, accents supprimes,
/// caracteres non alphanumeriques en separateurs, hachage FNV-1a 32 bits des mots ET
/// des bigrammes, incrementation de la coordonnee <c>hash % dimension</c>, puis
/// normalisation L2. Ne l'ameliore pas sans mettre a jour le contrat : des tests en
/// dependent, et un embedding qui change en douce est exactement la panne que ce
/// depot cherche a enseigner.
///
/// POURQUOI LA GRAINE DE PROJECTION EXISTE. Sans elle, l'Infrastructure ne fournirait
/// qu'UN seul faux : deux instances de meme dimension rendraient le MEME vecteur pour le
/// meme texte, donc une derive nulle, et le scenario B serait force de faire varier la
/// DIMENSION pour mesurer quoi que ce soit — ce qui fait tomber l'index avec une erreur
/// franche et demontre exactement l'inverse de ce qu'il faut montrer. La graine entre
/// dans l'offset de base FNV-1a : elle ne change ni le determinisme, ni la norme, ni le
/// repliement des accents, ni le fait que deux textes proches restent proches ; elle
/// change seulement SUR QUELLE coordonnee chaque mot atterrit. Deux graines a dimension
/// egale donnent donc deux modeles factices distincts et egalement plausibles — la
/// situation exacte de deux vrais modeles concurrents en dimension 1024.
/// </remarks>
public sealed class HashingEmbeddingService : IEmbeddingService
{
    /// <summary>Offset de base canonique de FNV-1a : c'est la graine du modele « hashing-fake ».</summary>
    public const uint DefaultSeed = 2166136261;

    private const uint FnvPrime = 16777619;

    private readonly uint _seed;

    /// <param name="dimension">Nombre de coordonnees du vecteur produit.</param>
    /// <param name="name">Nom du modele, tel qu'il partira dans les metadonnees de l'index.</param>
    /// <param name="seed">
    /// Offset de base du hachage, donc fonction de projection. Deux graines differentes
    /// dispersent les memes mots sur des coordonnees differentes : c'est ce qui permet
    /// d'avoir deux faux distincts A DIMENSION EGALE. La valeur par defaut est l'offset
    /// canonique de FNV-1a — elle reproduit a l'identique les vecteurs historiques.
    /// </param>
    public HashingEmbeddingService(int dimension = 256, string name = "hashing-fake", uint seed = DefaultSeed)
    {
        if (dimension <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(dimension), dimension, "La dimension d'un vecteur doit etre strictement positive.");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException(
                "Le modele factice doit porter un nom : il finit dans les metadonnees de l'index " +
                "et dans l'empreinte de configuration des instantanes.",
                nameof(name));
        }

        _seed = seed;
        Model = new EmbeddingModelDescriptor(name.Trim(), dimension);
    }

    public EmbeddingModelDescriptor Model { get; }

    /// <summary>
    /// La graine de projection effective. Elle est publique parce qu'elle fait partie de
    /// l'identite du faux au meme titre que sa dimension : deux services de meme nom et
    /// de graines differentes ne sont pas le meme modele.
    /// </summary>
    public uint Seed => _seed;

    /// <summary>
    /// Question et document sont encodes de facon identique. Les vrais modeles font
    /// souvent la difference (prefixes d'instruction, encodeurs asymetriques) : la
    /// distinction reste dans le port pour que le passage au reel ne change pas la
    /// signature, meme si ce faux-ci ne s'en sert pas.
    /// </summary>
    public Task<EmbeddingVector> EmbedQueryAsync(string text, CancellationToken cancellationToken = default) =>
        Task.FromResult(Embed(text));

    public Task<IReadOnlyList<EmbeddingVector>> EmbedDocumentsAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        if (texts is null)
        {
            throw new ArgumentNullException(nameof(texts));
        }

        var vectors = new EmbeddingVector[texts.Count];
        for (var i = 0; i < texts.Count; i++)
        {
            vectors[i] = Embed(texts[i]);
        }

        return Task.FromResult<IReadOnlyList<EmbeddingVector>>(vectors);
    }

    private EmbeddingVector Embed(string? text)
    {
        var values = new float[Model.Dimension];
        var words = Tokenize(text);

        foreach (var word in words)
        {
            Accumulate(values, word);
        }

        for (var i = 0; i + 1 < words.Count; i++)
        {
            Accumulate(values, string.Concat(words[i], " ", words[i + 1]));
        }

        NormaliseL2(values);

        // Un texte vide donne un vecteur nul de la bonne dimension : CosineSimilarity
        // rend alors 0, ce qui est la reponse honnete (« aucune direction »), la ou une
        // exception ferait tomber toute une indexation pour un paragraphe blanc.
        return EmbeddingVector.From(values);
    }

    /// <summary>
    /// Minuscules, accents supprimes, tout ce qui n'est pas alphanumerique devient une
    /// frontiere de mot.
    /// </summary>
    private static List<string> Tokenize(string? text)
    {
        var words = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return words;
        }

        var folded = TextNormalization.Fold(text);
        var builder = new StringBuilder(16);

        foreach (var c in folded)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(c);
                continue;
            }

            if (builder.Length > 0)
            {
                words.Add(builder.ToString());
                builder.Clear();
            }
        }

        if (builder.Length > 0)
        {
            words.Add(builder.ToString());
        }

        return words;
    }

    private void Accumulate(float[] values, string token)
    {
        var index = (int)(Fnv1a(token) % (uint)values.Length);
        values[index] += 1f;
    }

    /// <summary>
    /// FNV-1a 32 bits sur les octets UTF-8 du jeton, demarre sur la graine du service. Le
    /// detail « octets UTF-8 et non unites UTF-16 » est arbitraire mais doit rester stable :
    /// c'est lui qui garantit qu'un instantane enregistre aujourd'hui se compare a celui de
    /// la semaine prochaine. La graine, elle, est le seul degre de liberte assume : a graine
    /// egale la fonction est figee, et deux graines donnent deux projections independantes.
    /// </summary>
    private uint Fnv1a(string token)
    {
        var hash = _seed;

        foreach (var b in Encoding.UTF8.GetBytes(token))
        {
            unchecked
            {
                hash ^= b;
                hash *= FnvPrime;
            }
        }

        return hash;
    }

    /// <summary>
    /// Normalisation L2 : sans elle, un long paragraphe l'emporterait sur une phrase
    /// courte par sa seule longueur, et le cosinus ne mesurerait plus la ressemblance
    /// mais le bavardage.
    /// </summary>
    private static void NormaliseL2(float[] values)
    {
        double sumOfSquares = 0;
        foreach (var value in values)
        {
            sumOfSquares += (double)value * value;
        }

        if (sumOfSquares <= 0)
        {
            return;
        }

        var norm = Math.Sqrt(sumOfSquares);
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = (float)(values[i] / norm);
        }
    }
}
