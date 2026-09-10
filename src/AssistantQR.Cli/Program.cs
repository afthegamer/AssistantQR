using System.Text;

using AssistantQR.Cli.Commands;
using AssistantQR.Cli.Rendering;

using AssistantQR.Domain;

using AssistantQR.Infrastructure.Http;

namespace AssistantQR.Cli;

/// <summary>
/// Point d'entree de la couche de presentation.
/// </summary>
/// <remarks>
/// CE FICHIER EST VOLONTAIREMENT LE PLUS PAUVRE DU DEPOT. Il choisit une commande, la
/// laisse travailler, et traduit ce qui remonte en code de retour. Il ne connait ni le
/// corpus, ni les vecteurs, ni les politiques de reponse : la presentation lit la
/// configuration, compose les dependances, traduit des arguments en commandes de cas
/// d'usage et met en forme des resultats. Si une regle metier apparaissait ici, elle serait
/// invisible aux tests du Domain — et c'est exactement le glissement que ce depot enseigne
/// a repErer.
///
/// LA TRADUCTION DES EXCEPTIONS EST LE SEUL ENDROIT DU PROGRAMME QUI PARLE DE PILES
/// D'APPELS. Une pile d'appels affichee a l'utilisateur est un aveu : elle dit « je n'ai
/// pas prevu ce cas ». Les pannes attendues — service injoignable, modele absent, dossier
/// introuvable, index vide — ont chacune une phrase francaise qui dit quoi faire, et c'est
/// l'adaptateur concerne qui l'a redigee, parce que lui seul sait ce qui manque.
/// </remarks>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        UseUtf8();

        var renderer = ConsoleRenderer.ForConsole();
        using var cancellation = new CancellationTokenSource();

        Console.CancelKeyPress += (_, eventArgs) =>
        {
            // On annule proprement plutot que de laisser le systeme tuer le processus :
            // une indexation interrompue au milieu doit pouvoir le dire.
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            return await DispatchAsync(CommandLine.Parse(args), renderer, cancellation.Token).ConfigureAwait(false);
        }
        catch (UsageException exception)
        {
            renderer.Failure(exception.Message);
            renderer.Failure("Tape « assistantqr help » pour la syntaxe complete.");
            return ExitCodes.Usage;
        }
        catch (OperationCanceledException)
        {
            renderer.Failure("Interrompu.");
            return ExitCodes.Execution;
        }
        catch (Exception exception)
        {
            renderer.Failure(Explain(exception));
            return ExitCodes.Execution;
        }
    }

    /// <summary>
    /// Le dispatcher : un mot-cle, une commande. Pas de reflexion, pas d'attributs, pas
    /// d'enregistrement automatique — la liste des commandes doit se lire d'un trait.
    /// </summary>
    private static Task<int> DispatchAsync(
        CommandLine commandLine,
        ConsoleRenderer renderer,
        CancellationToken cancellationToken)
    {
        var verb = commandLine.Positional(0);

        if (verb is null || commandLine.Has("help"))
        {
            return HelpCommand.RunAsync(renderer);
        }

        return verb switch
        {
            "doctor" => DoctorCommand.RunAsync(commandLine, renderer, cancellationToken),
            "corpus" => CorpusCommand.RunAsync(commandLine, renderer, cancellationToken),
            "index" => IndexCommand.RunAsync(commandLine, renderer, cancellationToken),
            "ask" => AskCommand.RunAsync(commandLine, renderer, cancellationToken),
            "snapshot" => SnapshotCommand.RunAsync(commandLine, renderer, cancellationToken),
            "demo" => DemoCommand.RunAsync(commandLine, renderer, cancellationToken),
            "ports" => PortsCommand.RunAsync(commandLine, renderer, cancellationToken),
            "help" => HelpCommand.RunAsync(renderer),
            _ => throw new UsageException(
                $"Commande inconnue : « {verb} ». Commandes disponibles : " +
                "doctor, corpus, index, ask, snapshot, demo, ports, help."),
        };
    }

    /// <summary>
    /// Traduit une exception en une phrase que l'on peut agir. L'ordre des cas va du plus
    /// specifique au plus general : les adaptateurs ont deja redige un message utile, on se
    /// contente de le laisser passer.
    /// </summary>
    private static string Explain(Exception exception) => exception switch
    {
        EmbeddingServiceException or VectorIndexException =>
            exception.Message + Environment.NewLine +
            "Diagnostic complet : assistantqr doctor. Pour travailler sans service Python : --profile offline.",

        LanguageModelException =>
            exception.Message + Environment.NewLine +
            "Diagnostic complet : assistantqr doctor. Pour travailler sans Ollama : --llm extractive-fake.",

        HttpRequestException =>
            "Un service local n'a pas repondu : " + exception.Message + Environment.NewLine +
            "Verifie qu'il tourne (assistantqr doctor), ou bascule sur --profile offline.",

        TaskCanceledException or TimeoutException =>
            "Un service local a mis trop de temps a repondre." + Environment.NewLine +
            "Augmente le delai dans la configuration, ou bascule sur --profile offline.",

        DirectoryNotFoundException or FileNotFoundException =>
            exception.Message + Environment.NewLine +
            "Les chemins sont resolus depuis le binaire, puis depuis la racine du depot " +
            "(le dossier qui contient AssistantQR.sln).",

        InvalidDataException => exception.Message,

        DomainException => "Donnee refusee par le Domain : " + exception.Message,

        // PipelineOptions.Validate, le montage des dependances et les fabriques
        // d'infrastructure passent tous par ces deux types, avec un message deja redige.
        ArgumentException argument => StripParameterName(argument.Message),

        InvalidOperationException => exception.Message,

        _ => $"Erreur inattendue ({exception.GetType().Name}) : {exception.Message}",
    };

    /// <summary>
    /// <c>ArgumentException</c> ajoute « (Parameter 'x') » a la fin de son message. Ce
    /// suffixe s'adresse au developpeur qui lit une pile d'appels, pas a l'utilisateur qui
    /// vient de taper une valeur invalide : on le retire.
    /// </summary>
    private static string StripParameterName(string message)
    {
        var marker = message.LastIndexOf("(Parameter '", StringComparison.Ordinal);

        return marker <= 0 ? message : message[..marker].TrimEnd();
    }

    /// <summary>
    /// Le texte est en francais : sans UTF-8, la console de Windows remplace les accents
    /// par des points d'interrogation et les messages deviennent penibles a lire. L'appel
    /// echoue quand la sortie est redirigee vers un flux qui impose son encodage : ce n'est
    /// pas une raison d'interrompre le programme.
    /// </summary>
    private static void UseUtf8()
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (Exception exception) when (exception is IOException or PlatformNotSupportedException)
        {
            // On garde l'encodage en place.
        }
    }
}
