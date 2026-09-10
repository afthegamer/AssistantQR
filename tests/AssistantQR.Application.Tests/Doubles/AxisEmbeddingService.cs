using AssistantQR.Application.Model;
using AssistantQR.Application.Ports;

namespace AssistantQR.Application.Tests.Doubles;

/// <summary>
/// Service d'embeddings qui rend toujours <see cref="UnitVectors.Query"/>, quel que
/// soit le texte.
///
/// Il forme un couple avec <see cref="UnitVectors"/> : l'index factice contient des
/// vecteurs a deux composantes dont le cosinus avec cet axe vaut exactement le score
/// voulu. Le pipeline complet tourne donc sur de vrais calculs de similarite, tout en
/// laissant l'auteur du test choisir le classement au centieme pres.
///
/// C'est une doublure DEGENEREE, et il faut le dire en cours : elle ne teste rien de la
/// pertinence semantique. Ce n'est pas son travail. Les tests du cas d'usage portent sur
/// l'enchainement, les refus et les traces ; la qualite de la recherche se mesure
/// ailleurs, avec <see cref="FakeEmbeddingService"/> qui, lui, est lexicalement
/// plausible.
/// </summary>
public sealed class AxisEmbeddingService : IEmbeddingService
{
    /// <summary>Construit un service nomme, de dimension 2 comme les vecteurs qu'il rend.</summary>
    public AxisEmbeddingService(string name = "axis-fake") => Model = new EmbeddingModelDescriptor(name, 2);

    /// <inheritdoc />
    public EmbeddingModelDescriptor Model { get; }

    /// <summary>Nombre d'encodages de question demandes.</summary>
    public int EmbedQueryCallCount { get; private set; }

    /// <inheritdoc />
    public Task<EmbeddingVector> EmbedQueryAsync(string text, CancellationToken cancellationToken = default)
    {
        EmbedQueryCallCount++;
        return Task.FromResult(UnitVectors.Query);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<EmbeddingVector>> EmbedDocumentsAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        var vectors = new List<EmbeddingVector>(texts.Count);
        for (var i = 0; i < texts.Count; i++)
        {
            vectors.Add(UnitVectors.Query);
        }

        return Task.FromResult<IReadOnlyList<EmbeddingVector>>(vectors);
    }
}
