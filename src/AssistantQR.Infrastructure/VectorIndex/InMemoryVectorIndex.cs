using AssistantQR.Application.Model;
using AssistantQR.Application.Ports;

namespace AssistantQR.Infrastructure.VectorIndex;

/// <summary>
/// Adaptateur FACTICE de <see cref="IVectorIndex"/> : tout tient dans un dictionnaire,
/// la recherche est une force brute qui calcule le cosinus contre chaque morceau.
/// </summary>
/// <remarks>
/// POURQUOI LA FORCE BRUTE EST LE BON CHOIX ICI : sur un corpus de vingt-trois documents,
/// une recherche exhaustive est exacte, instantanee et — surtout — sans reglage. Un vrai
/// index approche (HNSW, IVF) introduit des parametres qui font varier les resultats sans
/// qu'aucune regle metier n'ait change ; le cours a deja de quoi faire avec le decoupage
/// et le modele d'embeddings. Cette doublure donne donc la reponse *exacte*, ce qui en
/// fait aussi l'etalon contre lequel comparer le vrai index.
///
/// Ce faux applique <see cref="SearchFilter"/> pour de bon : c'est indispensable, sinon
/// la demonstration pre-filtrage contre post-filtrage ne se declencherait jamais hors
/// ligne. Et, comme son homologue HTTP, il ne verifie JAMAIS que le vecteur de requete
/// vient du modele qui a construit l'index : la panne silencieuse doit rester
/// reproductible avec le profil « offline ».
///
/// Le verrou est volontairement grossier — un seul <c>lock</c> autour de l'etat, la
/// similarite calculee hors verrou sur une copie. Sur quelques centaines de morceaux
/// c'est amplement suffisant, et ca reste lisible pour un lecteur qui decouvre le code.
/// </remarks>
public sealed class InMemoryVectorIndex : IVectorIndex
{
    private readonly object _gate = new();
    private readonly Dictionary<string, IndexedChunk> _chunks = new(StringComparer.Ordinal);
    private readonly IClock? _clock;

    private string _embeddingModel = string.Empty;
    private int _dimension;
    private string _chunkingStrategyId = string.Empty;
    private DateTimeOffset? _builtAt;

    public InMemoryVectorIndex()
    {
    }

    /// <summary>
    /// Variante horlogee : la date de construction de l'index est la seule donnee non
    /// deterministe de cette classe. L'injecter permet a un test de la figer.
    /// </summary>
    public InMemoryVectorIndex(IClock clock) => _clock = clock;

    public Task ResetAsync(
        EmbeddingModelDescriptor model,
        string chunkingStrategyId,
        CancellationToken cancellationToken = default)
    {
        if (model is null)
        {
            throw new ArgumentNullException(nameof(model));
        }

        lock (_gate)
        {
            _chunks.Clear();
            _embeddingModel = model.Name;
            _dimension = model.Dimension;
            _chunkingStrategyId = chunkingStrategyId ?? string.Empty;

            // Un index vide n'a pas de date de construction : c'est ce qui distingue
            // « jamais indexe » de « indexe puis vide ».
            _builtAt = null;
        }

        return Task.CompletedTask;
    }

    public Task UpsertAsync(IReadOnlyList<IndexedChunk> chunks, CancellationToken cancellationToken = default)
    {
        if (chunks is null)
        {
            throw new ArgumentNullException(nameof(chunks));
        }

        if (chunks.Count == 0)
        {
            return Task.CompletedTask;
        }

        lock (_gate)
        {
            foreach (var indexed in chunks)
            {
                if (_dimension > 0 && indexed.Vector.Dimension != _dimension)
                {
                    // Controle a l'ECRITURE seulement. Refuser un vecteur de mauvaise taille
                    // a l'insertion protege l'integrite de l'index sans rien changer au
                    // scenario de la panne silencieuse, qui se joue a la LECTURE, avec un
                    // vecteur de bonne dimension mais d'un autre modele.
                    throw new InvalidOperationException(
                        $"Le morceau « {indexed.Chunk.ChunkId} » porte un vecteur de dimension " +
                        $"{indexed.Vector.Dimension} alors que l'index a ete remis a zero en {_dimension}. " +
                        "Relance l'indexation apres avoir aligne la configuration du modele d'embeddings.");
                }

                _chunks[indexed.Chunk.ChunkId] = indexed;
            }

            if (_dimension == 0 && chunks.Count > 0)
            {
                _dimension = chunks[0].Vector.Dimension;
            }

            _builtAt = _clock?.UtcNow ?? DateTimeOffset.UtcNow;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ScoredFragment>> SearchAsync(
        EmbeddingVector query,
        int topK,
        SearchFilter filter,
        CancellationToken cancellationToken = default)
    {
        if (topK < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(topK), topK, "Le nombre de voisins demandes doit valoir au moins 1.");
        }

        List<IndexedChunk> candidates;

        lock (_gate)
        {
            candidates = new List<IndexedChunk>(_chunks.Values);
        }

        var maximum = filter as SearchFilter.MaxAccessLevel;
        var scored = new List<ScoredFragment>(candidates.Count);

        foreach (var indexed in candidates)
        {
            // Pre-filtrage : l'index refuse de renvoyer ce que le demandeur n'a pas le
            // droit de lire. C'est bien une regle metier qui s'est invitee dans un
            // composant d'infrastructure — le prix a payer pour ne jamais transporter
            // un extrait interdit hors de l'index.
            if (maximum is not null && !indexed.Chunk.AccessLevel.IsReadableWith(maximum.Level))
            {
                continue;
            }

            var score = EmbeddingVector.CosineSimilarity(query, indexed.Vector);
            scored.Add(new ScoredFragment(indexed.Chunk.ToEvidence(), score));
        }

        // Tri par score decroissant, puis par identifiant de morceau : le second critere
        // n'a aucun sens metier, il sert uniquement a rendre les egalites deterministes.
        // Sans lui, deux executions identiques pourraient produire deux instantanes
        // differents, et la detection de derive crierait au loup.
        var ordered = scored
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Fragment.DocumentId.Value, StringComparer.Ordinal)
            .ThenBy(item => item.Fragment.Ordinal)
            .Take(topK)
            .ToList();

        return Task.FromResult<IReadOnlyList<ScoredFragment>>(ordered);
    }

    public Task<IndexMetadata> GetMetadataAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(new IndexMetadata(
                _embeddingModel,
                _dimension,
                _chunkingStrategyId,
                _chunks.Count,
                _builtAt));
        }
    }
}
