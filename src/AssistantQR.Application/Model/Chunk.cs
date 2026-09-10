using AssistantQR.Domain.Access;
using AssistantQR.Domain.Documents;
using AssistantQR.Domain.Evidence;

namespace AssistantQR.Application.Model;

/// <summary>
/// Un morceau de document. Concept d'APPLICATION : le decoupage est une decision
/// de mecanique de recherche (cf. principe CACE — <i>Changing Anything Changes
/// Everything</i>), pas une regle metier. Passer de 600 a 300 caracteres par morceau
/// change les reponses du systeme sans qu'aucune regle metier n'ait bouge : c'est la
/// demonstration qu'un tel reglage n'a rien a faire dans le Domain.
/// Le morceau recopie le titre et le niveau du document parce que l'index vectoriel
/// ne connait que des morceaux : il n'a aucun moyen de remonter au document d'origine.
/// </summary>
public sealed record Chunk(
    string ChunkId,
    DocumentId DocumentId,
    string DocumentTitle,
    AccessLevel AccessLevel,
    int Ordinal,
    string Text)
{
    /// <summary>Identifiant stable d'un morceau : « identifiant-du-document#rang ».</summary>
    public static string BuildId(DocumentId documentId, int ordinal) => $"{documentId}#{ordinal}";

    /// <summary>
    /// Texte reellement soumis au service d'embeddings : l'identifiant et le titre du
    /// document, puis le morceau.
    /// </summary>
    /// <remarks>
    /// POURQUOI LE SUJET DU DOCUMENT ENTRE DANS LE VECTEUR ALORS QU'IL N'EST PAS DANS LE
    /// MORCEAU. Le decoupage detache chaque paragraphe de ce qui le nomme. « Traitement
    /// interne des retards » est le titre du document ; aucun de ses paragraphes ne
    /// reprend ces mots ensemble — ils disent « relance », « regie municipale »,
    /// « dossier ». Sans cet ajout, « Comment sont traites les retards en interne ? » ne
    /// recoupait plus rien et l'index remontait quatre morceaux hors sujet : le sujet du
    /// document etait perdu A L'INDEXATION, pas a la recherche. Aucun reglage de topK ni
    /// de seuil ne rattrape ce qui n'a jamais ete vectorise.
    ///
    /// POURQUOI L'IDENTIFIANT EN PLUS DU TITRE. « gestion-retards-interne » se decoupe en
    /// « gestion », « retards », « interne » : c'est une seconde formulation du meme
    /// sujet, choisie par un humain, et elle double le poids des mots qui comptent. Sur
    /// un sac de mots sans ponderation, ou les mots vides (« comment », « sont », « les »)
    /// pesent autant que les mots porteurs, ce doublement est ce qui fait passer le bon
    /// document devant le bruit — le titre seul ne suffisait pas.
    ///
    /// C'est une decision de MECANIQUE DE RECHERCHE, au meme titre que le decoupage, et
    /// elle est donc ici et non dans le Domain. Elle est nommee plutot que fondue dans une
    /// concatenation au fil de l'eau : ce qu'on vectorise est un choix qui change les
    /// reponses, il merite d'etre lisible et attribuable.
    ///
    /// CE QU'ELLE COUTE, ET IL FAUT LE DIRE : identifiant et titre pesent dans TOUS les
    /// morceaux du document, y compris ceux qui parlent d'autre chose. Toute question qui
    /// reprend le titre rapproche donc le document ENTIER, paragraphes hors sujet compris.
    /// On echange une panne franche — le document jamais trouve — contre un biais diffus.
    /// C'est le bon echange ici, ce n'est pas un choix universel.
    ///
    /// <see cref="Text"/> reste intact : c'est lui qu'on affiche, qu'on cite et qu'on
    /// envoie au modele. Identifiant et titre n'entrent que dans le vecteur.
    /// </remarks>
    public string EmbeddingText => $"{DocumentId} {DocumentTitle}\n{Text}";

    /// <summary>
    /// Traduit le morceau en concept du Domain. La conversion perd volontairement
    /// l'identifiant de morceau : le Domain n'a pas a savoir que le decoupage existe.
    /// </summary>
    public EvidenceFragment ToEvidence() =>
        new(DocumentId, DocumentTitle, Text, AccessLevel, Ordinal);
}
