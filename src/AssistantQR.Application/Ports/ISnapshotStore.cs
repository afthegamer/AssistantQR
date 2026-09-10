using AssistantQR.Application.UseCases.Snapshots;

namespace AssistantQR.Application.Ports;

/// <summary>Conservation des instantanes d'evaluation.</summary>
/// <remarks>
/// POURQUOI LA FRONTIERE EST ICI.
/// Le port parle d'<see cref="EvaluationSnapshot"/>, pas de fichiers ni de JSON. C'est
/// le point entier : l'Application sait qu'un instantane se range et se relit par son
/// nom, elle ignore qu'il finit dans <c>snapshots/*.json</c>. La serialisation —
/// indentation, convention de nommage des champs, echappement des accents,
/// assainissement du nom de fichier — appartient a l'adaptateur. Aucun attribut de
/// serialisation ne remonte dans les types de l'Application, sinon la couche
/// dependrait du format retenu pour l'ecriture.
///
/// POURQUOI CE PORT EXISTE. Un systeme dont un composant est non deterministe ne peut
/// pas être valide par des assertions exactes ; il se surveille par comparaison. On
/// enregistre les reponses a un jeu de questions fixe, on change une chose — le
/// decoupage, la version du prompt, le modele, le mode de filtrage — et on mesure la
/// derive. Ce port est donc l'infrastructure de la seule methode d'evaluation
/// utilisable ici, pas un utilitaire de journalisation.
///
/// <c>LoadAsync</c> rend <c>null</c> plutot que de lever : demander un instantane
/// inexistant est un cas normal, pas une anomalie. C'est l'appelant qui decide si
/// l'absence est fatale.
/// </remarks>
public interface ISnapshotStore
{
    /// <summary>Enregistre un instantane, en ecrasant celui de même nom s'il existe.</summary>
    Task SaveAsync(EvaluationSnapshot snapshot, CancellationToken cancellationToken = default);

    /// <summary>Relit un instantane, ou <c>null</c> s'il n'existe pas.</summary>
    Task<EvaluationSnapshot?> LoadAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Les noms disponibles.</summary>
    Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default);
}
