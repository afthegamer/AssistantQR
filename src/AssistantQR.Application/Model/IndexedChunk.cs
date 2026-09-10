namespace AssistantQR.Application.Model;

/// <summary>
/// Un morceau et son vecteur, prets a être poses dans l'index.
/// Les deux sont assembles ici, dans l'Application, et pas dans l'adaptateur d'index :
/// c'est le pipeline qui decide quel modele d'embeddings sert a quoi, l'index se
/// contente de ranger ce qu'on lui donne. Un index qui calculerait lui-même ses
/// vecteurs rendrait le choix du modele invisible depuis le cas d'usage.
/// </summary>
public sealed record IndexedChunk(Chunk Chunk, EmbeddingVector Vector);
