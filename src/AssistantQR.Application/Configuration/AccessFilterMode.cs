namespace AssistantQR.Application.Configuration;

/// <summary>
/// Quand applique-t-on le controle d'acces : avant ou apres le classement par
/// similarite ? Ce reglage n'a l'air de rien et change pourtant les reponses du
/// systeme sur un corpus ou les niveaux partagent le vocabulaire.
/// </summary>
public enum AccessFilterMode
{
    /// <summary>
    /// L'index ne classe que ce que le demandeur peut lire. Meilleure pertinence :
    /// les <c>topK</c> resultats sont tous exploitables. Prix a payer : une regle
    /// metier est desormais appliquee par un composant technique.
    /// </summary>
    Pre = 0,

    /// <summary>
    /// L'index classe tout, l'Application ecarte ensuite l'interdit. Le Domain reste
    /// seul juge de l'acces. Prix a payer : les <c>topK</c> peuvent être integralement
    /// confidentiels, et l'on repond « rien de lisible » alors que le corpus contenait
    /// une reponse publique, classee onzieme. Defaut du projet, parce que la purete de
    /// la regle prime tant que la demonstration reste lisible.
    /// </summary>
    Post = 1,
}
