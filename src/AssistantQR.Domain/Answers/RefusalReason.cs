namespace AssistantQR.Domain.Answers;

/// <summary>
/// Les sept facons dont une demande peut echouer. L'enumeration est fermee et
/// exhaustive : un refus dont on ne saurait pas dire la cause serait inexplicable
/// a l'usager, donc inacceptable pour ce systeme.
/// </summary>
public enum RefusalReason
{
    /// <summary>Rien dans le corpus ne se rapproche de la question.</summary>
    NoEvidenceInCorpus = 0,

    /// <summary>Des extraits existent, mais aucun n'est lisible par ce demandeur.</summary>
    NoEvidenceReadableByRequester = 1,

    /// <summary>Le modèle n'a rien produit d'exploitable.</summary>
    ModelProducedEmptyAnswer = 2,

    /// <summary>Le modèle a repondu sans citer la moindre source.</summary>
    ModelProducedNoCitation = 3,

    /// <summary>Le modèle a cité un identifiant absent des extraits fournis (hallucination).</summary>
    ModelCitedUnknownDocument = 4,

    /// <summary>Le modèle s'est appuyé sur un document interdit a ce demandeur.</summary>
    ModelCitedForbiddenDocument = 5,

    /// <summary>
    /// Le modèle a lui-même declaré que les extraits ne permettaient pas de repondre,
    /// en emettant le marqueur de refus que le gabarit de prompt lui impose. Ce n'est
    /// pas une panne : c'est la regle « refuser plutot qu'inventer » qui s'applique.
    /// Le rang 6 est en fin d'enumeration a dessein : les instantanes JSON deja
    /// enregistres referencent ces causes par NOM, mais decaler les valeurs
    /// existantes serait un risque gratuit.
    /// </summary>
    ModelDeclinedToAnswer = 6,
}
