using System.Globalization;

using AssistantQR.Application.Configuration;

namespace AssistantQR.Application.UseCases.Snapshots;

/// <summary>
/// TOUT ce qui, sans être une regle metier, determine les reponses du systeme.
/// Ce type est la reponse a une question qu'on se pose toujours trop tard : « pourquoi
/// les reponses ont-elles change ? » Sans empreinte, on compare deux instantanes, on
/// constate une derive et on n'a aucun moyen de l'imputer. Avec elle, la difference
/// s'enonce : « decoupage paragraph → fixed-600-100 », « prompt 1.0.0 → 1.1.0 ».
/// Le principe est le même que celui d'un verrou de dependances : la reproductibilite
/// exige que l'on ecrive ce dont on depend, pas seulement ce que l'on fait.
/// </summary>
public sealed record ConfigurationFingerprint(
    string EmbeddingModel,
    int Dimension,
    string ChunkingStrategyId,
    AccessFilterMode FilterMode,
    int TopK,
    double MinScore,
    string PromptName,
    string PromptVersion,
    string PromptFingerprint,
    string LlmModelId,
    double Temperature,
    int? Seed)
{
    /// <summary>Empreinte neutre, pour un instantane dont la configuration n'a pas pu être etablie.</summary>
    public static ConfigurationFingerprint Empty { get; } = new(
        EmbeddingModel: string.Empty,
        Dimension: 0,
        ChunkingStrategyId: string.Empty,
        FilterMode: AccessFilterMode.Post,
        TopK: 0,
        MinScore: 0,
        PromptName: string.Empty,
        PromptVersion: string.Empty,
        PromptFingerprint: string.Empty,
        LlmModelId: string.Empty,
        Temperature: 0,
        Seed: null);

    /// <summary>
    /// Les differences avec une autre configuration, sous forme de phrases
    /// « champ : valeur d'origine → nouvelle valeur ». Liste vide si les deux
    /// configurations sont identiques.
    /// </summary>
    public IReadOnlyList<string> DifferencesWith(ConfigurationFingerprint other)
    {
        if (other is null)
        {
            return Array.Empty<string>();
        }

        var differences = new List<string>();

        Compare(differences, nameof(EmbeddingModel), EmbeddingModel, other.EmbeddingModel);
        Compare(differences, nameof(Dimension), Format(Dimension), Format(other.Dimension));
        Compare(differences, nameof(ChunkingStrategyId), ChunkingStrategyId, other.ChunkingStrategyId);
        Compare(differences, nameof(FilterMode), FilterMode.ToString(), other.FilterMode.ToString());
        Compare(differences, nameof(TopK), Format(TopK), Format(other.TopK));
        Compare(differences, nameof(MinScore), Format(MinScore), Format(other.MinScore));
        Compare(differences, nameof(PromptName), PromptName, other.PromptName);
        Compare(differences, nameof(PromptVersion), PromptVersion, other.PromptVersion);
        Compare(differences, nameof(PromptFingerprint), PromptFingerprint, other.PromptFingerprint);
        Compare(differences, nameof(LlmModelId), LlmModelId, other.LlmModelId);
        Compare(differences, nameof(Temperature), Format(Temperature), Format(other.Temperature));
        Compare(differences, nameof(Seed), Format(Seed), Format(other.Seed));

        return differences;
    }

    /// <summary>Une ligne compacte, lisible dans un terminal.</summary>
    public override string ToString() =>
        $"{EmbeddingModel}/{Format(Dimension)} · {ChunkingStrategyId} · {FilterMode.ToString().ToLowerInvariant()} · " +
        $"topK={Format(TopK)} · minScore={Format(MinScore)} · {PromptName}@{PromptVersion}#{PromptFingerprint} · " +
        $"{LlmModelId} · T={Format(Temperature)} · seed={Format(Seed)}";

    private static void Compare(ICollection<string> differences, string field, string mine, string theirs)
    {
        if (!string.Equals(mine, theirs, StringComparison.Ordinal))
        {
            differences.Add($"{field} : {Display(mine)} → {Display(theirs)}");
        }
    }

    private static string Display(string value) => value.Length == 0 ? "(vide)" : value;

    private static string Format(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Format(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "(aucune)";

    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
