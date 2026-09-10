using AssistantQR.Infrastructure.Configuration;

namespace AssistantQR.Infrastructure.Tests.Support;

/// <summary>
/// Chemins des artefacts reels du depot (corpus, gabarits, instantanes).
/// </summary>
/// <remarks>
/// Quelques tests ne portent pas sur un adaptateur mais sur les DONNEES du depot :
/// le corpus contient-il bien vingt-trois documents, le gabarit 1.0.0 declare-t-il les
/// trois placeholders attendus. Ce sont des tests de contrat sur des fichiers, et ils
/// meritent d'exister : le corpus et les prompts sont des entrees du systeme au meme
/// titre que le code, et rien d'autre ne les verifie.
///
/// On passe deliberement par <see cref="PathResolver"/> plutot que par un chemin
/// relatif bricole a coups de « ../../.. » : c'est le meme code que celui du montage,
/// donc un test qui trouve le corpus prouve accessoirement que l'application le
/// trouvera aussi.
/// </remarks>
internal static class RepositoryLayout
{
    public static string CorpusDirectory => PathResolver.Resolve("corpus");

    public static string PromptsDirectory => PathResolver.Resolve("prompts");
}
