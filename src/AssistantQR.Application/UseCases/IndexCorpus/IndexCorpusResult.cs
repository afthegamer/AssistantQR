namespace AssistantQR.Application.UseCases.IndexCorpus;

/// <summary>
/// Compte rendu d'une indexation.
/// Le modele et la strategie de decoupage y figurent parce qu'une indexation est une
/// operation dont on doit pouvoir dire, plus tard, sous quelles conditions elle a été
/// faite. Le temps passe dans les embeddings est isole du temps total : c'est presque
/// toujours lui qui domine, et le savoir evite de chercher la lenteur ailleurs.
/// </summary>
public sealed record IndexCorpusResult(
    int DocumentCount,
    int ChunkCount,
    string EmbeddingModel,
    int Dimension,
    string ChunkingStrategyId,
    TimeSpan Duration,
    TimeSpan EmbeddingDuration);
