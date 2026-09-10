using System.Diagnostics;

using AssistantQR.Application.Model;
using AssistantQR.Application.Ports;

using AssistantQR.Domain.Documents;

namespace AssistantQR.Application.UseCases.IndexCorpus;

/// <summary>
/// Construire l'index a partir du corpus : lire, decouper, vectoriser, ranger.
/// Les quatre etapes sont quatre ports differents, et c'est deliberé. On pourrait
/// ecrire un unique <c>IIndexBuilder</c> qui ferait tout ; on perdrait alors la
/// possibilite de changer de strategie de decoupage sans toucher au reste, et surtout
/// la possibilite de dire QUELLE etape a change quand les reponses derivent.
/// </summary>
public sealed class IndexCorpusUseCase
{
    /// <summary>Taille des lots d'encodage. Un aller-retour par morceau serait inutilisable en reseau.</summary>
    private const int EmbeddingBatchSize = 32;

    /// <summary>Taille des lots d'ecriture dans l'index.</summary>
    private const int UpsertBatchSize = 64;

    private readonly IDocumentRepository _documents;
    private readonly IChunkingStrategy _chunking;
    private readonly IEmbeddingService _embeddings;
    private readonly IVectorIndex _index;

    /// <summary>Les quatre ports de la chaine d'indexation.</summary>
    public IndexCorpusUseCase(
        IDocumentRepository documents,
        IChunkingStrategy chunking,
        IEmbeddingService embeddings,
        IVectorIndex index)
    {
        _documents = documents ?? throw new ArgumentNullException(nameof(documents));
        _chunking = chunking ?? throw new ArgumentNullException(nameof(chunking));
        _embeddings = embeddings ?? throw new ArgumentNullException(nameof(embeddings));
        _index = index ?? throw new ArgumentNullException(nameof(index));
    }

    /// <summary>Reconstruit integralement l'index.</summary>
    /// <exception cref="InvalidOperationException">
    /// Si le service d'embeddings ne rend pas autant de vecteurs qu'on lui a soumis de
    /// textes, ou si une dimension ne correspond pas a celle annoncee par le modele.
    /// </exception>
    public async Task<IndexCorpusResult> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var embeddingStopwatch = new Stopwatch();

        var documents = await _documents.GetAllAsync(cancellationToken).ConfigureAwait(false)
                        ?? Array.Empty<Document>();

        var chunks = new List<Chunk>();
        foreach (var document in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            chunks.AddRange(_chunking.Split(document));
        }

        // La remise a zero vient APRES le decoupage : si une strategie leve une
        // exception, on prefere un index intact et perime a un index vide.
        // Elle enregistre au passage avec quoi l'index est reconstruit — c'est cette
        // trace qui rendra detectable, plus tard, une interrogation avec un autre modele.
        await _index.ResetAsync(_embeddings.Model, _chunking.Id, cancellationToken).ConfigureAwait(false);

        var indexed = new List<IndexedChunk>(chunks.Count);

        for (var start = 0; start < chunks.Count; start += EmbeddingBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var count = Math.Min(EmbeddingBatchSize, chunks.Count - start);
            var batch = chunks.GetRange(start, count);

            // On vectorise EmbeddingText, pas Text : l'identifiant et le titre du document
            // accompagnent le morceau dans le vecteur. Voir Chunk.EmbeddingText pour ce
            // que ce choix repare et ce qu'il coute.
            var texts = new List<string>(batch.Count);
            foreach (var chunk in batch)
            {
                texts.Add(chunk.EmbeddingText);
            }

            embeddingStopwatch.Start();
            var vectors = await _embeddings.EmbedDocumentsAsync(texts, cancellationToken).ConfigureAwait(false);
            embeddingStopwatch.Stop();

            // Appariement par position : c'est le contrat du port. On le VERIFIE plutot
            // que de l'esperer, parce qu'un decalage d'un rang associerait chaque morceau
            // au vecteur du precedent — une corruption totalement silencieuse, qui ne se
            // manifesterait que par des reponses subtilement hors sujet.
            if (vectors is null || vectors.Count != texts.Count)
            {
                throw new InvalidOperationException(
                    $"Le service d'embeddings a renvoyé {vectors?.Count ?? 0} vecteur(s) pour {texts.Count} texte(s) : " +
                    "l'appariement morceau/vecteur est rompu, l'indexation est interrompue.");
            }

            for (var i = 0; i < batch.Count; i++)
            {
                var vector = vectors[i];
                if (vector.Dimension != _embeddings.Model.Dimension)
                {
                    throw new InvalidOperationException(
                        $"Le modèle « {_embeddings.Model.Name} » annonce une dimension de {_embeddings.Model.Dimension} " +
                        $"mais a produit un vecteur de dimension {vector.Dimension} pour le morceau « {batch[i].ChunkId} ».");
                }

                indexed.Add(new IndexedChunk(batch[i], vector));
            }
        }

        for (var start = 0; start < indexed.Count; start += UpsertBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var count = Math.Min(UpsertBatchSize, indexed.Count - start);
            await _index.UpsertAsync(indexed.GetRange(start, count), cancellationToken).ConfigureAwait(false);
        }

        stopwatch.Stop();

        return new IndexCorpusResult(
            DocumentCount: documents.Count,
            ChunkCount: indexed.Count,
            EmbeddingModel: _embeddings.Model.Name,
            Dimension: _embeddings.Model.Dimension,
            ChunkingStrategyId: _chunking.Id,
            Duration: stopwatch.Elapsed,
            EmbeddingDuration: embeddingStopwatch.Elapsed);
    }
}
