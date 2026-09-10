using AssistantQR.Application.Model;

namespace AssistantQR.Application.Ports;

/// <summary>Transformation de texte en vecteurs.</summary>
/// <remarks>
/// POURQUOI LA FRONTIERE EST ICI.
/// Derriere ce port peuvent se trouver un service HTTP Python appelant Ollama, une
/// bibliotheque ONNX embarquee, ou une fonction de hachage deterministe de trente
/// lignes. Le pipeline ne fait la difference sur aucune de ces trois. C'est ce qui
/// rend les tests hors ligne possibles — et surtout significatifs, puisque le
/// substitut par hachage produit une similarite lexicale plausible.
///
/// POURQUOI DEUX METHODES ET PAS UNE. <c>EmbedQueryAsync</c> et
/// <c>EmbedDocumentsAsync</c> font la même chose « en theorie ». En pratique, de
/// nombreux modeles (E5, BGE, Qwen3) exigent un prefixe different selon qu'on encode
/// une question ou un passage, et les ignorer degrade silencieusement la pertinence.
/// Cette asymetrie appartient au monde des modeles : elle doit apparaitre dans le
/// contrat, sinon chaque adaptateur devra deviner l'intention de l'appelant.
/// Le traitement par lot n'est pas une optimisation prematuree : un aller-retour HTTP
/// par morceau sur un corpus de plusieurs centaines de morceaux est inutilisable.
///
/// POURQUOI <c>Model</c> EST EXPOSE. Sans ce descripteur, l'Application ne pourrait
/// pas comparer le modele courant a celui qui a construit l'index. Un port qui cache
/// l'identite de ce qu'il y a derriere rend indetectable la panne la plus insidieuse
/// de ce projet.
/// </remarks>
public interface IEmbeddingService
{
    /// <summary>Identite du modele courant : nom et dimension produite.</summary>
    EmbeddingModelDescriptor Model { get; }

    /// <summary>Encode une question.</summary>
    Task<EmbeddingVector> EmbedQueryAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>Encode un lot de passages. L'ordre de sortie suit l'ordre d'entree.</summary>
    Task<IReadOnlyList<EmbeddingVector>> EmbedDocumentsAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default);
}
