using AssistantQR.Cli.Configuration;
using AssistantQR.Cli.Rendering;

using AssistantQR.Infrastructure.Diagnostics;

namespace AssistantQR.Cli.Commands;

/// <summary>
/// <c>assistantqr doctor</c> : l'etat de l'environnement, d'un seul coup d'oeil.
/// </summary>
/// <remarks>
/// C'est la premiere commande a taper apres un clone, et la seule dont la sortie doit
/// rester lisible quand tout va mal. Elle ne repare rien et ne decide rien : elle demande
/// son verdict a <c>EnvironmentProbe</c>, qui vit en Infrastructure parce que sonder un
/// service HTTP est une entree-sortie, et se contente de le mettre en colonnes.
/// </remarks>
internal static class DoctorCommand
{
    /// <summary>Sonde l'environnement et affiche le tableau des verdicts.</summary>
    public static async Task<int> RunAsync(
        CommandLine commandLine,
        ConsoleRenderer renderer,
        CancellationToken cancellationToken)
    {
        commandLine.EnsureKnownOptions(CliConfiguration.OverrideOptions);

        var options = CliConfiguration.Load(commandLine);
        using var host = CliHost.Create(options);

        renderer.Title("Diagnostic de l'environnement");

        renderer.Section("Configuration effective");
        renderer.Pairs(CliConfiguration.Describe(options));

        var results = await host.Service<EnvironmentProbe>().RunAsync(cancellationToken).ConfigureAwait(false);

        renderer.Section("Sondes");
        renderer.Table(
            new[] { "Composant", "Etat", "Detail" },
            results.Select(result => (IReadOnlyList<string>)new[]
            {
                result.Component,
                result.Ok ? "OK" : "ECHEC",
                result.Detail,
            }).ToList());

        var failures = results.Count(result => !result.Ok);

        renderer.Line();

        if (failures == 0)
        {
            // Le message depend du profil : annoncer « aucun service exterieur n'est
            // sollicite » alors qu'on vient d'interroger Ollama et le service Python
            // etait faux, et faux dans le sens le plus genant — il rassurait sur une
            // chaine hors ligne alors que la chaine reelle etait branchee.
            renderer.Paragraph(options.IsOffline
                ? "Environnement utilisable. En profil « offline », c'est normal : aucun service " +
                  "exterieur n'est sollicite, tout est calcule dans le processus."
                : "Environnement utilisable. Profil « local » : les reponses viendront des vrais " +
                  "services sondes ci-dessus. Si l'un d'eux tombe, la commande s'arretera avec une " +
                  "erreur — aucune doublure ne prendra le relais en silence.");
            return ExitCodes.Success;
        }

        renderer.Paragraph(
            $"{failures} sonde(s) en echec. Chaque ligne « ECHEC » dit ce qu'il manque et " +
            "comment le fournir. Pour travailler sans rien installer : --profile offline.");

        return ExitCodes.Execution;
    }
}
