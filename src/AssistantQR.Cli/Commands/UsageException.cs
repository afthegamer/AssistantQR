namespace AssistantQR.Cli.Commands;

/// <summary>La commande tapee n'a pas de sens : mauvaise option, argument manquant.</summary>
/// <remarks>
/// Ce type existe pour separer deux echecs que l'utilisateur ne vit pas de la meme
/// facon. Une erreur d'USAGE se corrige en retapant la commande, et merite le code de
/// retour 1 avec un rappel de la syntaxe. Une erreur d'EXECUTION — service injoignable,
/// dossier absent, index vide — se corrige en agissant sur l'environnement, et merite le
/// code 2 avec une phrase qui dit quoi faire. Confondre les deux oblige a lire la trace
/// pour savoir de quel cote chercher.
/// </remarks>
internal sealed class UsageException : Exception
{
    /// <summary>Construit l'erreur avec la phrase a montrer telle quelle.</summary>
    public UsageException(string message)
        : base(message)
    {
    }
}
