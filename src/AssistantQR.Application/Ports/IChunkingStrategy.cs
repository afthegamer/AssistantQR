using AssistantQR.Application.Model;

using AssistantQR.Domain.Documents;

namespace AssistantQR.Application.Ports;

/// <summary>Decoupage d'un document en morceaux indexables.</summary>
/// <remarks>
/// POURQUOI LA FRONTIERE EST ICI — et c'est le port le plus discutable des huit.
/// Decouper un texte ne demande ni reseau, ni disque, ni horloge : rien n'oblige
/// techniquement a en faire un port. On aurait pu ecrire les trois strategies
/// directement dans l'Application, comme on l'a fait pour <c>IRetrievalStrategy</c>.
///
/// La raison du choix inverse est pedagogique et assumee : le decoupage est le
/// reglage qui illustre le mieux le principe CACE. Passer de « paragraph » a
/// « fixed-600-100 » change les reponses du systeme sans qu'aucune regle metier n'ait
/// bouge. En faire un port force ce reglage a apparaitre dans la configuration, dans
/// l'injection de dependances et dans l'empreinte des instantanes — donc a devenir
/// visible et attribuable. Une strategie interne aurait le même effet fonctionnel
/// mais resterait un detail d'implementation qu'on oublie de citer quand les
/// resultats derivent.
///
/// <c>Id</c> n'est pas decoratif : il est enregistre dans les metadonnees de l'index
/// et dans l'empreinte de configuration. C'est lui qui permet de dire « les reponses
/// ont change parce qu'on a reindexe avec un autre decoupage ».
/// La methode est synchrone, contrairement aux autres ports : signer <c>Task</c> ici
/// serait mentir sur la nature de l'operation.
/// </remarks>
public interface IChunkingStrategy
{
    /// <summary>Identifiant stable : « paragraph », « fixed-600-100 », « whole-document ».</summary>
    string Id { get; }

    /// <summary>Description lisible, destinee aux traces et a la ligne de commande.</summary>
    string Description { get; }

    /// <summary>Decoupe un document en morceaux ordonnes.</summary>
    IReadOnlyList<Chunk> Split(Document document);
}
