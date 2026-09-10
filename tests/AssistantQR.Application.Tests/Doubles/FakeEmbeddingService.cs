using AssistantQR.Application.Model;
using AssistantQR.Application.Ports;

namespace AssistantQR.Application.Tests.Doubles;

/// <summary>
/// Service d'embeddings deterministe, hors ligne, sans cle d'API.
///
/// POURQUOI CETTE DOUBLURE VIT DANS LE PROJET DE TEST ET NON DANS L'INFRASTRUCTURE.
/// Le projet AssistantQR.Application.Tests ne reference QUE AssistantQR.Application.
/// C'est une contrainte de compilation, donc une preuve executable : si le coeur du
/// systeme avait besoin d'un client HTTP, d'une base vectorielle ou d'une variable
/// d'environnement, cette suite ne compilerait pas. Elle compile, donc il n'en a pas besoin.
///
/// L'algorithme est volontairement lexical (mots et bigrammes projetes par hachage)
/// et non aleatoire : deux textes qui partagent du vocabulaire obtiennent une
/// similarite elevee. Les tests de recherche sont donc SIGNIFICATIFS et pas seulement
/// deterministes — une doublure qui rendrait des vecteurs constants ferait passer
/// n'importe quel pipeline, y compris un pipeline casse.
///
/// Le <c>salt</c> est la piece maitresse de la demonstration de la panne silencieuse :
/// deux instances de MEME dimension mais de salt different sont deux modeles
/// d'embeddings incompatibles qui ne provoqueront jamais d'erreur de type.
/// </summary>
public sealed class FakeEmbeddingService : IEmbeddingService
{
    private readonly uint _salt;

    /// <summary>Construit un modele factice nomme, de dimension et de fonction de hachage choisies.</summary>
    public FakeEmbeddingService(string name = "fake-embeddings", int dimension = 32, uint salt = 0u)
    {
        if (dimension < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(dimension), dimension, "Dimension minimale : 1.");
        }

        Model = new EmbeddingModelDescriptor(name, dimension);
        _salt = salt;
    }

    /// <inheritdoc />
    public EmbeddingModelDescriptor Model { get; }

    /// <summary>Nombre d'appels d'encodage de question, pour les assertions.</summary>
    public int EmbedQueryCallCount { get; private set; }

    /// <summary>Nombre d'appels d'encodage par lot, pour les assertions.</summary>
    public int EmbedDocumentsCallCount { get; private set; }

    /// <summary>
    /// Les textes REELLEMENT soumis a l'encodage, dans l'ordre. Ce que le pipeline
    /// vectorise n'est pas forcement ce qu'il affichera ensuite : sans cette capture, un
    /// test ne peut pas distinguer « on a indexe le morceau » de « on a indexe le morceau
    /// precede de son titre », alors que les deux ne rendent pas les memes reponses.
    /// </summary>
    public List<string> SubmittedDocumentTexts { get; } = new();

    /// <summary>Nombre de vecteurs retires de chaque lot : simule un service qui perd des reponses.</summary>
    public int DropVectorsFromEachBatch { get; set; }

    /// <summary>Dimension reellement produite si elle doit mentir sur celle annoncee (0 = coherent).</summary>
    public int ProducedDimensionOverride { get; set; }

    /// <inheritdoc />
    public Task<EmbeddingVector> EmbedQueryAsync(string text, CancellationToken cancellationToken = default)
    {
        EmbedQueryCallCount++;
        return Task.FromResult(Embed(text));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<EmbeddingVector>> EmbedDocumentsAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        EmbedDocumentsCallCount++;

        var vectors = new List<EmbeddingVector>(texts.Count);
        foreach (var text in texts)
        {
            SubmittedDocumentTexts.Add(text);
            vectors.Add(Embed(text));
        }

        var kept = Math.Max(0, vectors.Count - DropVectorsFromEachBatch);
        return Task.FromResult<IReadOnlyList<EmbeddingVector>>(vectors.GetRange(0, kept));
    }

    /// <summary>Encodage synchrone, expose pour construire des attentes dans les tests.</summary>
    public EmbeddingVector Embed(string text)
    {
        var dimension = ProducedDimensionOverride > 0 ? ProducedDimensionOverride : Model.Dimension;
        var values = new float[dimension];
        var words = Tokenize(text);

        for (var i = 0; i < words.Count; i++)
        {
            values[(int)(Hash(words[i]) % (uint)dimension)] += 1f;

            // Le bigramme apporte un peu de sensibilite a l'ordre des mots : sans lui,
            // « le chat mange » et « mange le chat » seraient strictement identiques.
            if (i + 1 < words.Count)
            {
                values[(int)(Hash(words[i] + "_" + words[i + 1]) % (uint)dimension)] += 0.5f;
            }
        }

        NormalizeInPlace(values);
        return EmbeddingVector.From(values);
    }

    private static List<string> Tokenize(string? text)
    {
        var words = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return words;
        }

        var current = new System.Text.StringBuilder();
        foreach (var c in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                current.Append(c);
                continue;
            }

            if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            words.Add(current.ToString());
        }

        return words;
    }

    /// <summary>FNV-1a 32 bits, dont la base est decalee par le salt : changer le salt change le modele.</summary>
    private uint Hash(string value)
    {
        var hash = 2166136261u ^ _salt;
        foreach (var c in value)
        {
            hash ^= c;
            hash *= 16777619u;
        }

        return hash;
    }

    private static void NormalizeInPlace(float[] values)
    {
        double norm = 0;
        foreach (var value in values)
        {
            norm += value * value;
        }

        if (norm <= 0)
        {
            // Texte vide : on rend un vecteur nul de bonne dimension. La similarite
            // cosinus vaudra 0, ce qui est exactement le comportement souhaite.
            return;
        }

        var length = Math.Sqrt(norm);
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = (float)(values[i] / length);
        }
    }
}
