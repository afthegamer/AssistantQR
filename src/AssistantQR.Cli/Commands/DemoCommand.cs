using AssistantQR.Cli.Rendering;

namespace AssistantQR.Cli.Commands;

/// <summary>
/// <c>assistantqr demo</c> : les trois demonstrations que le cours doit pouvoir jouer en direct.
/// </summary>
/// <remarks>
/// CES COMMANDES NE PROUVENT RIEN QU'UN TEST NE PROUVERAIT. Leur interet est ailleurs :
/// elles rendent OBSERVABLE, dans un terminal et en une invocation, ce que les tests
/// verifient en silence. Un etudiant a qui l'on affirme « changer de modele ne touche pas
/// le pipeline » a le droit de ne pas y croire ; la meme phrase, avec deux colonnes de
/// resultats identiques sous les yeux, se discute autrement.
///
/// TOUTES TROIS FONCTIONNENT EN PROFIL « offline », sans Ollama ni service Python. C'est
/// une contrainte de conception, pas une commodite : une demonstration qui exige une
/// installation prealable n'est jouee par personne.
/// </remarks>
internal static class DemoCommand
{
    /// <summary>Aiguille les sous-commandes du groupe « demo ».</summary>
    public static Task<int> RunAsync(
        CommandLine commandLine,
        ConsoleRenderer renderer,
        CancellationToken cancellationToken)
    {
        var subCommand = commandLine.Positional(1);

        return subCommand switch
        {
            "llm-swap" => LlmSwapDemo.RunAsync(commandLine, renderer, cancellationToken),
            "embedding-swap" => EmbeddingSwapDemo.RunAsync(commandLine, renderer, cancellationToken),
            "access-filter" => AccessFilterDemo.RunAsync(commandLine, renderer, cancellationToken),
            null => throw new UsageException(
                "Sous-commande manquante. Attendu : demo llm-swap, demo embedding-swap, " +
                "ou demo access-filter \"<question>\"."),
            _ => throw new UsageException(
                $"Demonstration inconnue : « demo {subCommand} ». " +
                "Attendu : llm-swap, embedding-swap ou access-filter."),
        };
    }
}
