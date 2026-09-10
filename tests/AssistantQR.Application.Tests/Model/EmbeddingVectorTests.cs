using AssistantQR.Application.Model;

using Xunit;

namespace AssistantQR.Application.Tests.Model;

/// <summary>
/// Le cosinus est la seule mathematique du projet. Elle vit en Application parce
/// qu'aucun adaptateur n'aurait de raison legitime d'en donner une autre definition —
/// et parce qu'elle se teste en trois lignes, sans reseau ni cle d'API.
/// </summary>
public sealed class EmbeddingVectorTests
{
    private const double Tolerance = 1e-6;

    [Fact]
    public void CosineSimilarity_IdenticalVectors_ReturnsOne()
    {
        var vector = EmbeddingVector.From(new[] { 1f, 2f, 3f });

        Assert.Equal(1d, EmbeddingVector.CosineSimilarity(vector, vector), Tolerance);
    }

    [Fact]
    public void CosineSimilarity_OrthogonalVectors_ReturnsZero()
    {
        var left = EmbeddingVector.From(new[] { 1f, 0f });
        var right = EmbeddingVector.From(new[] { 0f, 1f });

        Assert.Equal(0d, EmbeddingVector.CosineSimilarity(left, right), Tolerance);
    }

    [Fact]
    public void CosineSimilarity_OppositeVectors_ReturnsMinusOne()
    {
        var left = EmbeddingVector.From(new[] { 1f, 1f });
        var right = EmbeddingVector.From(new[] { -1f, -1f });

        Assert.Equal(-1d, EmbeddingVector.CosineSimilarity(left, right), Tolerance);
    }

    [Fact]
    public void CosineSimilarity_IgnoresMagnitude_OnlyDirectionMatters()
    {
        var unit = EmbeddingVector.From(new[] { 1f, 0f });
        var stretched = EmbeddingVector.From(new[] { 17f, 0f });

        Assert.Equal(1d, EmbeddingVector.CosineSimilarity(unit, stretched), Tolerance);
    }

    [Fact]
    public void CosineSimilarity_MismatchedDimensions_Throws()
    {
        var small = EmbeddingVector.From(new[] { 1f, 0f });
        var large = EmbeddingVector.From(new[] { 1f, 0f, 0f });

        // La dimension est le SEUL garde-fou automatique contre l'usage de deux modeles
        // d'embeddings differents. Quand elle coincide — cas frequent : 768, 1024 sont
        // des tailles banales — plus rien n'empeche la panne silencieuse.
        var error = Assert.Throws<InvalidOperationException>(
            () => EmbeddingVector.CosineSimilarity(small, large));

        Assert.Contains("2", error.Message, StringComparison.Ordinal);
        Assert.Contains("3", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CosineSimilarity_NullVector_ReturnsZeroInsteadOfNaN()
    {
        var zero = EmbeddingVector.From(new[] { 0f, 0f, 0f });
        var other = EmbeddingVector.From(new[] { 1f, 2f, 3f });

        // Un vecteur nul n'a pas de direction : la division rendrait NaN, qui se
        // propagerait ensuite dans les tris et les seuils sans jamais lever d'erreur.
        Assert.Equal(0d, EmbeddingVector.CosineSimilarity(zero, other), Tolerance);
        Assert.Equal(0d, EmbeddingVector.CosineSimilarity(zero, zero), Tolerance);
    }

    [Fact]
    public void From_EmptyArray_Throws()
    {
        Assert.Throws<ArgumentException>(() => EmbeddingVector.From(Array.Empty<float>()));
    }

    [Fact]
    public void From_Enumerable_ProducesSameVectorAsArray()
    {
        var fromArray = EmbeddingVector.From(new[] { 0.5f, -0.25f });
        var fromEnumerable = EmbeddingVector.From(new List<float> { 0.5f, -0.25f });

        Assert.Equal(fromArray, fromEnumerable);
        Assert.Equal(2, fromEnumerable.Dimension);
    }

    [Fact]
    public void From_CopiesInput_MutatingTheSourceDoesNotChangeTheVector()
    {
        var source = new[] { 1f, 0f };
        var vector = EmbeddingVector.From(source);

        source[0] = 99f;

        Assert.Equal(1f, vector.ToArray()[0]);
    }

    [Fact]
    public void IsEmpty_DefaultValue_IsTrue()
    {
        var uninitialized = default(EmbeddingVector);

        Assert.True(uninitialized.IsEmpty);
        Assert.Equal(0, uninitialized.Dimension);
    }

    [Fact]
    public void Equals_SameContentComputedTwice_AreEqual()
    {
        // L'egalite doit porter sur le CONTENU : sans cela, deux appels distincts au
        // meme service d'embeddings produiraient des vecteurs « differents » et les
        // assertions de reproductibilite deviendraient impossibles a ecrire.
        var left = EmbeddingVector.From(new[] { 0.1f, 0.2f });
        var right = EmbeddingVector.From(new[] { 0.1f, 0.2f });

        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }
}
