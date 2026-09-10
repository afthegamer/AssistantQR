namespace AssistantQR.Application.Model;

/// <summary>
/// TENSION 1 (non-determinisme) : temperature et graine sont EXPLICITES dans le
/// contrat. Un port qui les cacherait rendrait le non-determinisme invisible.
/// Le composant le moins fiable du systeme serait alors aussi le seul dont on ne
/// verrait pas les reglages — exactement l'inverse de ce qu'il faut faire.
/// Consequence pratique : ces deux valeurs remontent jusque dans l'empreinte de
/// configuration d'un instantane, donc une derive due a une temperature relevee
/// est attribuable au lieu d'être mysterieuse.
/// </summary>
public sealed record LlmRequest(
    string Prompt,
    double Temperature,
    int? Seed,
    int MaxTokens,
    IReadOnlyList<string>? Stop = null);
