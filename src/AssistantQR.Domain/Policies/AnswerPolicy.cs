// ---------------------------------------------------------------------------
// POURQUOI CE FICHIER EST LE COEUR DU COURS
//
// Cette fonction encode les trois regles metier de l'assistant : on ne repond
// jamais sans citer (1), on ne cité jamais un document absent des extraits
// fournis (2), on ne s'appuie jamais sur un document interdit au demandeur (3).
// Elle prend des donnees et rend des donnees : pas d'appel reseau, pas de cle
// d'API, pas de base vectorielle, pas d'horloge. On peut donc la tester
// exhaustivement, en quelques millisecondes, hors ligne, et obtenir toujours le
// meme resultat — alors que le modèle de langue, lui, est non deterministe.
// C'est tout l'interet de la Clean Architecture appliquee a l'IA : la partie du
// systeme dont la justesse est verifiable est isolee de la partie qui ne l'est
// pas. Ce que le modèle produit n'est qu'une PROPOSITION soumise a ce juge-ci.
// ---------------------------------------------------------------------------

using AssistantQR.Domain.Access;
using AssistantQR.Domain.Answers;
using AssistantQR.Domain.Documents;
using AssistantQR.Domain.Evidence;
using AssistantQR.Domain.Questions;

namespace AssistantQR.Domain.Policies;

/// <summary>
/// Le coeur metier. Aucune I/O, aucun modèle, aucune dependance : entree pure, sortie pure.
/// Tout le cours tient dans le fait que CETTE fonction est testable sans cle d'API.
/// </summary>
public static class AnswerPolicy
{
    /// <summary>
    /// Confronte la proposition du modèle aux extraits reellement fournis et a
    /// l'habilitation du demandeur. Les huit regles sont evaluees dans un ordre
    /// fixe : la premiere qui echoue determine la cause du refus.
    /// </summary>
    public static AnswerOutcome Decide(
        Question question,
        Requester requester,
        IReadOnlyList<EvidenceFragment> suppliedEvidence,
        DraftAnswer draft,
        int excerptLength = 240)
    {
        // 1. Rien n'a été trouve : la question sort du corpus.
        if (suppliedEvidence is null || suppliedEvidence.Count == 0)
        {
            return Refuse(
                RefusalReason.NoEvidenceInCorpus,
                "Aucun document du corpus ne permet d'appuyer une réponse à cette question.");
        }

        // 2. Des extraits existent, mais l'habilitation du demandeur les exclut tous.
        //    Ce refus-la est different du precedent : le systeme sait, mais ne dira pas.
        var readable = AccessPolicy.Readable(suppliedEvidence, requester);
        if (readable.Count == 0)
        {
            return Refuse(
                RefusalReason.NoEvidenceReadableByRequester,
                $"Des documents traitent de cette question, mais aucun n'est accessible au niveau d'habilitation « {requester.Clearance} ».");
        }

        // 3. Le modèle a DECLARE ne pas pouvoir repondre.
        //
        //    POURQUOI CETTE DISTINCTION VIT ICI, ET PAS DANS L'ADAPTATEUR.
        //    Emettre le marqueur de refus, c'est OBEIR : le gabarit de prompt ordonne au
        //    modèle de le faire quand les extraits ne suffisent pas, et cet ordre n'est
        //    que la regle metier « refuser plutot qu'inventer » ecrite en langue
        //    naturelle. Le refus declare est donc une ISSUE METIER prevue, au meme titre
        //    qu'une réponse. « Le modèle n'a rien rendu », a l'inverse, est un incident
        //    technique : delai depasse, sortie tronquee, fournisseur muet. Confondre les
        //    deux ferait apparaitre une regle metier qui fonctionne sous les traits d'une
        //    panne dans les journaux — et rendrait impossible de mesurer l'une comme
        //    l'autre. L'adaptateur sait TRADUIRE le marqueur ; seul le Domain peut dire
        //    ce qu'il SIGNIFIE.
        //
        //    Cette branche vient apres les regles 1 et 2, et c'est voulu : si aucun
        //    extrait n'etait lisible, c'est le controle d'acces qui a tranche, pas le
        //    modèle — que l'on n'a d'ailleurs meme pas appele.
        if (draft is not null && draft.Declined)
        {
            return Refuse(
                RefusalReason.ModelDeclinedToAnswer,
                "Le modèle a jugé lui-même que les extraits fournis ne permettaient pas de répondre "
                + "et a préféré s'abstenir plutôt que d'inventer.");
        }

        // 4. Le modèle n'a rien produit d'exploitable.
        if (draft is null || string.IsNullOrWhiteSpace(draft.Text))
        {
            return Refuse(
                RefusalReason.ModelProducedEmptyAnswer,
                "Le modèle n'a produit aucun texte exploitable à partir des extraits fournis.");
        }

        // 5. REGLE METIER 1 : pas de citation, pas de réponse.
        var citedIds = draft.CitedDocumentIds ?? Array.Empty<DocumentId>();
        if (citedIds.Count == 0)
        {
            return Refuse(
                RefusalReason.ModelProducedNoCitation,
                "Le modèle a rédigé un texte sans citer la moindre source : une réponse non sourcée est rejetée.");
        }

        // 6. REGLE METIER 2 : un identifiant cité qui n'apparait dans aucun extrait
        //    fourni est une invention pure du modèle.
        var suppliedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fragment in suppliedEvidence)
        {
            suppliedIds.Add(fragment.DocumentId.Value);
        }

        foreach (var citedId in citedIds)
        {
            if (!suppliedIds.Contains(citedId.Value))
            {
                return Refuse(
                    RefusalReason.ModelCitedUnknownDocument,
                    $"Le modèle a cité « {citedId} », qui ne figure dans aucun extrait fourni : la citation est inventée.");
            }
        }

        // 7. REGLE METIER 3 : l'identifiant existe bien, mais tous ses fragments sont
        //    hors de portee du demandeur. Le modèle a donc lu ce qu'il n'aurait pas du.
        var readableIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fragment in readable)
        {
            readableIds.Add(fragment.DocumentId.Value);
        }

        foreach (var citedId in citedIds)
        {
            if (!readableIds.Contains(citedId.Value))
            {
                return Refuse(
                    RefusalReason.ModelCitedForbiddenDocument,
                    $"Le modèle s'est appuyé sur « {citedId} », dont l'accès est refusé au niveau d'habilitation « {requester.Clearance} ».");
            }
        }

        // 8. Tout tient : on materialise les citations. Pour chaque identifiant cité,
        //    dans l'ordre du modèle, on retient le PREMIER fragment lisible portant cet
        //    identifiant. Les identifiants repetes sont ignores : citer deux fois la
        //    meme source n'ajoute pas de source.
        var citations = new List<Citation>(citedIds.Count);
        var alreadyCited = new HashSet<string>(StringComparer.Ordinal);

        foreach (var citedId in citedIds)
        {
            if (!alreadyCited.Add(citedId.Value))
            {
                continue;
            }

            foreach (var fragment in readable)
            {
                if (fragment.DocumentId.Value == citedId.Value)
                {
                    citations.Add(Citation.FromEvidence(fragment, excerptLength));
                    break;
                }
            }
        }

        return new AnswerOutcome.Answered(Answer.Create(draft.Text, citations));
    }

    private static AnswerOutcome Refuse(RefusalReason reason, string explanation) =>
        new AnswerOutcome.Refused(reason, explanation);
}
