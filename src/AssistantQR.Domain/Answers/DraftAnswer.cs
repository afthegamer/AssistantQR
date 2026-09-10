using AssistantQR.Domain.Documents;

namespace AssistantQR.Domain.Answers;

/// <summary>
/// Ce que le modèle pretend avoir produit : un texte, une liste d'identifiants cites,
/// et le fait — ou non — qu'il ait DECLARE ne pas pouvoir repondre.
/// C'est une PROPOSITION, pas une réponse. Elle doit passer AnswerPolicy pour le devenir.
/// Distinguer les deux types est ce qui empeche la sortie d'un modèle de langue de
/// se faire passer pour une verite du systeme.
///
/// <para>
/// <c>Declined</c> porte une information que le texte seul ne peut pas porter : un
/// modèle qui emet le marqueur de refus rend un texte vide, exactement comme un modèle
/// muet. Sans ce booleen, l'obeissance a la regle metier et la panne technique
/// arriveraient au Domain sous la même forme — indiscernables.
/// </para>
/// <para>
/// Le parametre reste optionnel et en dernier : les constructions existantes a deux
/// arguments continuent de compiler et signifient « non declare ».
/// </para>
/// </summary>
public sealed record DraftAnswer(
    string Text,
    IReadOnlyList<DocumentId> CitedDocumentIds,
    bool Declined = false)
{
    /// <summary>Proposition vide : ce que l'on soumet a la politique quand on n'a meme pas appele le modèle.</summary>
    public static DraftAnswer Empty { get; } = new(string.Empty, Array.Empty<DocumentId>());

    /// <summary>
    /// Proposition de refus DECLARE : le modèle a repondu, et sa réponse est « je ne peux
    /// pas repondre avec ces extraits ». La fabrique ne peut pas s'appeler <c>Declined</c>
    /// — ce nom est deja celui de la propriete — d'ou <c>DeclinedByModel</c>.
    /// </summary>
    public static DraftAnswer DeclinedByModel { get; } =
        new(string.Empty, Array.Empty<DocumentId>(), Declined: true);
}
