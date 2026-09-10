namespace AssistantQR.Application.Ports;

/// <summary>Lecture de l'heure courante.</summary>
/// <remarks>
/// POURQUOI UNE INTERFACE POUR UNE SEULE PROPRIETE.
/// <c>DateTimeOffset.UtcNow</c> est une entree-sortie deguisee : c'est un appel au
/// systeme d'exploitation, dont le resultat change a chaque invocation et qu'aucun
/// test ne controle. Ecrit en dur dans un cas d'usage, il rend ce cas d'usage
/// non deterministe — et c'est precisement ce que ce projet cherche a eviter,
/// puisque son sujet est de circonscrire le non-determinisme du modele de langue.
/// Il serait absurde de discipliner le composant probabiliste tout en laissant
/// l'horloge entrer par la fenetre.
///
/// OU CELA COMPTE CONCRETEMENT. Un instantane porte une date de creation. Sans ce
/// port, deux instantanes du même jeu de questions differeraient toujours, et la
/// comparaison — dont c'est le seul objet — serait inutilisable en test. Avec une
/// horloge fixe, l'instantane devient reproductible au caractere pres.
///
/// C'est le port le plus petit du projet, et c'est justement ce qui en fait un bon
/// exemple : la taille d'une frontiere ne dit rien de son utilite. Ce qui compte est
/// ce qu'elle rend remplacable.
/// </remarks>
public interface IClock
{
    /// <summary>Instant courant, en UTC.</summary>
    DateTimeOffset UtcNow { get; }
}
