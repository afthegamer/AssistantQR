namespace AssistantQR.Application.Model;

/// <summary>
/// NOTE PEDAGOGIQUE : ce type est en Application et PAS en Domain, deliberement.
/// Un vecteur n'est pas un concept du metier « repondre en citant ses sources » ;
/// c'est le mecanisme de recherche qu'on a choisi. Le Domain l'ignore totalement.
/// La preuve par l'absurde : si demain on remplace la recherche vectorielle par
/// une recherche plein texte BM25, AnswerPolicy ne change pas d'une seule ligne,
/// alors que tout ce fichier disparait. Ce qui disparait quand la technique change
/// n'avait rien a faire dans le Domain.
/// </summary>
public readonly record struct EmbeddingVector
{
    private readonly ReadOnlyMemory<float> _values;

    private EmbeddingVector(ReadOnlyMemory<float> values) => _values = values;

    /// <summary>Composantes du vecteur, en lecture seule.</summary>
    public ReadOnlyMemory<float> Values => _values;

    /// <summary>Nombre de composantes. Deux vecteurs de dimensions differentes ne se comparent pas.</summary>
    public int Dimension => _values.Length;

    /// <summary>Vrai pour <c>default(EmbeddingVector)</c> et pour tout vecteur sans composante.</summary>
    public bool IsEmpty => _values.Length == 0;

    /// <summary>Construit un vecteur en recopiant la sequence fournie.</summary>
    /// <exception cref="ArgumentException">Si la sequence est nulle ou vide.</exception>
    public static EmbeddingVector From(IEnumerable<float> values)
    {
        if (values is null)
        {
            throw new ArgumentException(
                "Un vecteur d'embedding ne peut pas être construit à partir de rien.", nameof(values));
        }

        return From(values as float[] ?? values.ToArray());
    }

    /// <summary>Construit un vecteur en recopiant le tableau fourni (la copie evite un aliasing surprenant).</summary>
    /// <exception cref="ArgumentException">Si le tableau est nul ou vide.</exception>
    public static EmbeddingVector From(float[] values)
    {
        if (values is null || values.Length == 0)
        {
            throw new ArgumentException(
                "Un vecteur d'embedding vide n'a pas de sens : la dimension doit être connue.", nameof(values));
        }

        var copy = new float[values.Length];
        Array.Copy(values, copy, values.Length);
        return new EmbeddingVector(copy);
    }

    /// <summary>Copie defensive des composantes, pour les adaptateurs qui doivent les serialiser.</summary>
    public float[] ToArray() => _values.ToArray();

    /// <summary>
    /// Similarite cosinus, la mesure de proximite retenue par ce projet.
    /// Elle est ici et pas derriere un port : c'est une operation mathematique pure,
    /// aucun adaptateur n'aurait de raison legitime d'en donner une autre definition.
    /// </summary>
    /// <exception cref="InvalidOperationException">Si les deux vecteurs n'ont pas la même dimension.</exception>
    public static double CosineSimilarity(EmbeddingVector a, EmbeddingVector b)
    {
        if (a.Dimension != b.Dimension)
        {
            throw new InvalidOperationException(
                $"Comparaison impossible : dimensions différentes ({a.Dimension} contre {b.Dimension}). " +
                "C'est le symptôme d'un index construit avec un autre modèle d'embeddings.");
        }

        var left = a._values.Span;
        var right = b._values.Span;

        double dot = 0;
        double normLeft = 0;
        double normRight = 0;

        for (var i = 0; i < left.Length; i++)
        {
            double x = left[i];
            double y = right[i];
            dot += x * y;
            normLeft += x * x;
            normRight += y * y;
        }

        if (normLeft <= 0 || normRight <= 0)
        {
            // Un vecteur nul n'a pas de direction : la similarite n'est pas definie, on rend 0.
            return 0;
        }

        return dot / (Math.Sqrt(normLeft) * Math.Sqrt(normRight));
    }

    /// <summary>
    /// Egalite par CONTENU et non par reference de tampon : deux vecteurs identiques
    /// calcules par deux appels distincts doivent être egaux, sinon les tests
    /// deviendraient dependants de l'allocation.
    /// </summary>
    public bool Equals(EmbeddingVector other) =>
        _values.Length == other._values.Length && _values.Span.SequenceEqual(other._values.Span);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(_values.Length);

        var span = _values.Span;
        var sampled = Math.Min(span.Length, 8);
        for (var i = 0; i < sampled; i++)
        {
            hash.Add(span[i]);
        }

        return hash.ToHashCode();
    }

    /// <inheritdoc />
    public override string ToString() => $"vecteur[{Dimension}]";
}
