using AssistantQR.Application.Ports;
using AssistantQR.Application.UseCases.Snapshots;

namespace AssistantQR.Application.Tests.Doubles;

/// <summary>
/// Depot d'instantanes en memoire.
///
/// Il illustre ce que le port <see cref="ISnapshotStore"/> protege : l'Application
/// range et relit des <see cref="EvaluationSnapshot"/> par leur nom, un point c'est
/// tout. Aucune notion de fichier, de JSON, d'indentation ou d'echappement d'accents
/// ne remonte jusqu'ici — et c'est bien pour cela que ce dictionnaire de trente lignes
/// peut remplacer integralement l'adaptateur reel dans toute la suite de tests.
/// </summary>
public sealed class FakeSnapshotStore : ISnapshotStore
{
    private readonly Dictionary<string, EvaluationSnapshot> _snapshots = new(StringComparer.Ordinal);
    private readonly List<EvaluationSnapshot> _saved = new();

    /// <summary>Tous les enregistrements recus, dans l'ordre, doublons compris.</summary>
    public IReadOnlyList<EvaluationSnapshot> Saved => _saved;

    /// <summary>Depose un instantane sans passer par le cas d'usage.</summary>
    public FakeSnapshotStore With(EvaluationSnapshot snapshot)
    {
        _snapshots[snapshot.Name] = snapshot;
        return this;
    }

    /// <inheritdoc />
    public Task SaveAsync(EvaluationSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        _saved.Add(snapshot);
        _snapshots[snapshot.Name] = snapshot;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<EvaluationSnapshot?> LoadAsync(string name, CancellationToken cancellationToken = default) =>
        Task.FromResult(_snapshots.TryGetValue(name, out var snapshot) ? snapshot : null);

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default)
    {
        var names = new List<string>(_snapshots.Keys);
        names.Sort(StringComparer.Ordinal);
        return Task.FromResult<IReadOnlyList<string>>(names);
    }
}
