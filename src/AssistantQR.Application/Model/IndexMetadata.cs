namespace AssistantQR.Application.Model;

/// <summary>
/// Ce que l'index sait de lui-même : avec quel modele et quelle strategie de decoupage
/// il a été construit, combien de morceaux il contient, et quand.
/// Ce type existe pour UNE raison : rendre detectable le scenario ou l'on interroge un
/// index construit avec un autre modele d'embeddings. Les dimensions concordent, la
/// recherche repond, les scores sont plausibles — et les resultats sont faux. Sans
/// metadonnees exposees, cette panne est parfaitement silencieuse.
/// </summary>
public sealed record IndexMetadata(
    string EmbeddingModel,
    int Dimension,
    string ChunkingStrategyId,
    int ChunkCount,
    DateTimeOffset? BuiltAt)
{
    /// <summary>Index jamais construit. <c>BuiltAt</c> nul : on ne fabrique pas une date par defaut.</summary>
    public static IndexMetadata Empty { get; } = new(string.Empty, 0, string.Empty, 0, null);
}
