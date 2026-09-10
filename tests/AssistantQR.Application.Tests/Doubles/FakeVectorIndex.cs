using AssistantQR.Application.Model;
using AssistantQR.Application.Ports;

using AssistantQR.Domain.Access;
using AssistantQR.Domain.Documents;

namespace AssistantQR.Application.Tests.Doubles;

/// <summary>
/// Index vectoriel en memoire : force brute, cosinus, tri decroissant, <c>topK</c>.
///
/// Il HONORE <see cref="SearchFilter"/>, et c'est le point le plus important de cette
/// doublure. Le pre-filtrage confie une regle metier — le controle d'acces — a un
/// composant technique. Si cette doublure trichait en ignorant le filtre, la
/// difference entre pre et post-filtrage disparaitrait et la demonstration centrale
/// du cours n'aurait plus de support executable.
///
/// Il journalise ses appels (<see cref="Calls"/>) parce qu'un cas d'usage se juge
/// aussi sur ce qu'il fait DANS QUEL ORDRE : remettre l'index a zero apres avoir
/// commence a y ecrire serait une faute qu'aucune assertion sur le resultat final ne
/// revelerait.
/// </summary>
public sealed class FakeVectorIndex : IVectorIndex
{
    /// <summary>Date de construction figee : un index factice ne doit pas dependre de l'horloge.</summary>
    public static readonly DateTimeOffset BuiltAtStub = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

    private readonly List<IndexedChunk> _items = new();
    private readonly List<string> _calls = new();

    /// <summary>Journal des appels recus, dans l'ordre : « Reset », « Upsert(12) », « Search(4) »...</summary>
    public IReadOnlyList<string> Calls => _calls;

    /// <summary>Nombre de morceaux actuellement indexes.</summary>
    public int Count => _items.Count;

    /// <summary>Metadonnees rendues par l'index. Modifiable pour simuler un index d'une autre provenance.</summary>
    public IndexMetadata Metadata { get; set; } = IndexMetadata.Empty;

    /// <summary>Le dernier filtre recu, pour verifier ce que la strategie a reellement demande.</summary>
    public SearchFilter? LastFilter { get; private set; }

    /// <summary>
    /// Fait mentir l'index : il accepte le filtre d'acces puis l'ignore. C'est le
    /// scenario que le pre-filtrage rend possible et que le Domain ne peut pas tester —
    /// sauf a repasser lui-meme par la politique, ce que fait la defense en profondeur.
    /// </summary>
    public bool IgnoreAccessFilterContract { get; set; }

    /// <summary>
    /// Fait mentir l'index sur son tri : il promet un classement decroissant et rend
    /// l'inverse. Une garantie tenue par un adaptateur externe n'est pas une garantie.
    /// </summary>
    public bool BreakSortContract { get; set; }

    /// <summary>Ajoute un morceau deja vectorise.</summary>
    public void Seed(Chunk chunk, EmbeddingVector vector) => _items.Add(new IndexedChunk(chunk, vector));

    /// <summary>
    /// Ajoute un morceau dont le score face a <see cref="UnitVectors.Query"/> est choisi
    /// a la main. Le score n'est pas stocke : il est obtenu par un vrai calcul de cosinus.
    /// </summary>
    public void SeedScored(
        string documentId,
        string title,
        AccessLevel accessLevel,
        double score,
        string? text = null,
        int ordinal = 0)
    {
        var id = DocumentId.From(documentId);
        var chunk = new Chunk(
            Chunk.BuildId(id, ordinal),
            id,
            title,
            accessLevel,
            ordinal,
            text ?? $"Extrait du document {documentId}.");

        Seed(chunk, UnitVectors.WithCosine(score));
    }

    /// <inheritdoc />
    public Task ResetAsync(
        EmbeddingModelDescriptor model,
        string chunkingStrategyId,
        CancellationToken cancellationToken = default)
    {
        _calls.Add("Reset");
        _items.Clear();
        Metadata = new IndexMetadata(model.Name, model.Dimension, chunkingStrategyId, 0, BuiltAtStub);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UpsertAsync(IReadOnlyList<IndexedChunk> chunks, CancellationToken cancellationToken = default)
    {
        _calls.Add($"Upsert({chunks.Count})");
        _items.AddRange(chunks);
        Metadata = Metadata with { ChunkCount = _items.Count };
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ScoredFragment>> SearchAsync(
        EmbeddingVector query,
        int topK,
        SearchFilter filter,
        CancellationToken cancellationToken = default)
    {
        _calls.Add($"Search({topK})");
        LastFilter = filter;

        var scored = new List<ScoredFragment>(_items.Count);

        foreach (var item in _items)
        {
            // LE PRE-FILTRAGE, VU DE L'INTERIEUR DE L'INDEX. C'est ici, dans un composant
            // technique, qu'une regle de securite est appliquee. Le Domain n'a aucun
            // moyen de verifier cette ligne : c'est exactement le prix a payer.
            if (!IgnoreAccessFilterContract &&
                filter is SearchFilter.MaxAccessLevel maximum &&
                !item.Chunk.AccessLevel.IsReadableWith(maximum.Level))
            {
                continue;
            }

            scored.Add(new ScoredFragment(
                item.Chunk.ToEvidence(),
                EmbeddingVector.CosineSimilarity(query, item.Vector)));
        }

        scored.Sort(static (left, right) => right.Score.CompareTo(left.Score));

        var kept = scored.Count > topK ? scored.GetRange(0, topK) : scored;

        if (BreakSortContract)
        {
            kept = new List<ScoredFragment>(kept);
            kept.Reverse();
        }

        return Task.FromResult<IReadOnlyList<ScoredFragment>>(kept);
    }

    /// <inheritdoc />
    public Task<IndexMetadata> GetMetadataAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Metadata);
}
