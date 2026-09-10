using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AssistantQR.Application.Model;

/// <summary>
/// TENSION 3 : un prompt est un artefact versionne, nomme et empreinte —
/// pas une chaine litterale perdue au fond d'une methode.
/// Un prompt est du CODE au sens ou il determine le comportement du systeme, mais
/// c'est de la DONNEE au sens ou il se modifie sans recompiler. Le traiter comme un
/// artefact de premiere classe (nom, version, empreinte) est ce qui permet de dire,
/// trois mois plus tard, pourquoi les reponses ont change.
/// </summary>
public sealed record PromptTemplate(
    string Name,
    string Version,
    string Body,
    IReadOnlyList<string> RequiredPlaceholders)
{
    private static readonly Regex PlaceholderPattern =
        new(@"\{\{\s*([A-Za-z0-9_\-\.]+)\s*\}\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AnyPlaceholderPattern =
        new(@"\{\{.*?\}\}", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private string? _fingerprint;

    /// <summary>
    /// Constructeur de copie explicite. Le champ d'empreinte n'est deliberement PAS
    /// recopie : un <c>with { Body = ... }</c> produit un autre prompt, et laisser
    /// trainer l'empreinte du precedent transformerait le cache en mensonge.
    /// </summary>
    private PromptTemplate(PromptTemplate original)
    {
        Name = original.Name;
        Version = original.Version;
        Body = original.Body;
        RequiredPlaceholders = original.RequiredPlaceholders;
    }

    /// <summary>SHA-256 du corps, 12 premiers caracteres hexadecimaux minuscules.</summary>
    public string Fingerprint => _fingerprint ??= ComputeFingerprint(Body);

    /// <summary>
    /// Remplace <c>{{cle}}</c> par la valeur correspondante.
    /// La double verification (placeholders requis presents en entree, aucun
    /// <c>{{...}}</c> residuel en sortie) est volontairement stricte : un prompt
    /// envoye avec un trou dedans produit une reponse plausible et fausse, c'est-a-dire
    /// le pire des bogues.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Si un placeholder requis manque dans <paramref name="values"/>, ou s'il subsiste
    /// un <c>{{...}}</c> non resolu apres remplacement.
    /// </exception>
    public string Render(IReadOnlyDictionary<string, string> values)
    {
        var provided = values ?? new Dictionary<string, string>(StringComparer.Ordinal);

        var missing = new List<string>();
        foreach (var required in RequiredPlaceholders)
        {
            if (!provided.ContainsKey(required))
            {
                missing.Add(required);
            }
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Le gabarit « {Name}@{Version} » exige des valeurs absentes : {string.Join(", ", missing)}.");
        }

        var rendered = PlaceholderPattern.Replace(
            Body,
            match => provided.TryGetValue(match.Groups[1].Value, out var value) ? value : match.Value);

        var leftover = AnyPlaceholderPattern.Match(rendered);
        if (leftover.Success)
        {
            throw new InvalidOperationException(
                $"Le gabarit « {Name}@{Version} » contient encore un emplacement non résolu : {leftover.Value}.");
        }

        return rendered;
    }

    /// <summary>Reduit le gabarit a ce qui doit apparaitre dans une trace ou un instantane.</summary>
    public PromptDescriptor Describe() => new(Name, Version, Fingerprint);

    private static string ComputeFingerprint(string body)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(body ?? string.Empty));
        return Convert.ToHexString(hash).ToLowerInvariant()[..12];
    }
}
