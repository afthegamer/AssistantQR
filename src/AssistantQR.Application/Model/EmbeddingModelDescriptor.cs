namespace AssistantQR.Application.Model;

/// <summary>
/// Carte d'identite du modele d'embeddings : son nom et la dimension qu'il produit.
/// Ce descripteur circule dans le pipeline parce que c'est lui qui permet de detecter
/// qu'un index a été construit avec un AUTRE modele — la panne silencieuse que le
/// cours veut rendre visible. Sans ce type, l'information resterait enfermee dans
/// l'adaptateur et personne ne pourrait la comparer.
/// </summary>
public sealed record EmbeddingModelDescriptor(string Name, int Dimension);
