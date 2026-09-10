using AssistantQR.Application.Ports;
using AssistantQR.Application.UseCases.Snapshots;

namespace AssistantQR.Infrastructure.Snapshots;

/// <summary>
/// Adaptateur FACTICE de <see cref="ISnapshotStore"/> : les instantanes vivent en memoire.
/// </summary>
/// <remarks>
/// Verifier que l'enregistrement d'un instantane produit bien une entree par question,
/// ou que la comparaison detecte un passage de reponse a refus, n'a rien a voir avec
/// des fichiers. Passer par le disque ajouterait a ces tests un dossier temporaire a
/// creer et a nettoyer, une dependance a l'ordre d'execution, et une possibilite
/// d'echec qui ne dit rien sur le comportement teste.
///
/// Le double conserve la semantique du port sur le seul point ou elle est observable :
/// <c>SaveAsync</c> ecrase l'instantane de meme nom, et <c>LoadAsync</c> rend
/// <c>null</c> sur un nom inconnu au lieu de lever.
/// </remarks>
public sealed class InMemorySnapshotStore : ISnapshotStore
{
    private readonly object _lock = new();
    private readonly Dictionary<string, EvaluationSnapshot> _snapshots = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task SaveAsync(EvaluationSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        lock (_lock)
        {
            _snapshots[snapshot.Name] = snapshot;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<EvaluationSnapshot?> LoadAsync(string name, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return Task.FromResult<EvaluationSnapshot?>(_snapshots.GetValueOrDefault(name ?? string.Empty));
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var names = new List<string>(_snapshots.Keys);
            names.Sort(StringComparer.Ordinal);

            return Task.FromResult<IReadOnlyList<string>>(names);
        }
    }
}
