using AssistantQR.Cli.Configuration;
using AssistantQR.Cli.Rendering;

using AssistantQR.Application.Ports;

namespace AssistantQR.Cli.Commands;

/// <summary>
/// <c>assistantqr ports</c> : les huit frontieres du systeme, et l'adaptateur branche
/// derriere chacune.
/// </summary>
/// <remarks>
/// C'EST UNE COMMANDE DE COURS, ET ELLE L'ASSUME. Elle ne sert a rien en exploitation.
/// Elle sert a projeter, en une page, la totalite de ce que ce systeme sait du monde
/// exterieur : huit interfaces, huit implementations, et une phrase par frontiere disant
/// pourquoi elle est la plutot qu'un cran plus haut ou plus bas.
///
/// LA COLONNE « ADAPTATEUR ACTIF » EST RESOLUE DANS LE CONTENEUR, PAS ECRITE EN DUR. Elle
/// change donc avec le profil et avec les noms de modeles configures — ce qui rend la
/// these du scenario A verifiable en deux invocations : la meme commande, avec
/// <c>--profile local</c>, affiche trois lignes differentes et zero ligne de code modifiee.
///
/// Les justifications reprennent, condensees, les blocs <c>remarks</c> des interfaces
/// elles-memes. Elles sont recopiees plutot que lues a l'execution : la documentation XML
/// n'est pas generee dans ce depot, et faire dependre une commande de la presence d'un
/// fichier .xml compile serait une fragilite gratuite.
/// </remarks>
internal static class PortsCommand
{
    private static readonly (Type Contract, string Justification)[] Ports =
    {
        (typeof(IDocumentRepository),
            "Le pipeline veut des documents deja valides. Qu'ils viennent de fichiers Markdown a en-tete " +
            "YAML, et que « interne » doive devenir Internal, est une affaire d'adaptateur. Un cran plus " +
            "bas (des flux, des chemins) ferait fuir la notion de fichier dans l'Application ; un cran plus " +
            "haut (des morceaux deja decoupes) enterrerait une decision de pertinence dans un lecteur."),

        (typeof(IChunkingStrategy),
            "Le port le plus discutable des huit : decouper un texte ne demande ni reseau ni disque. Il est " +
            "port par choix pedagogique — c'est le reglage qui illustre le mieux le principe CACE, et en " +
            "faire une frontiere l'oblige a apparaitre dans la configuration, dans le montage et dans " +
            "l'empreinte des instantanes, donc a devenir attribuable quand les reponses derivent."),

        (typeof(IEmbeddingService),
            "Derriere : un service HTTP, une bibliotheque embarquee, ou trente lignes de hachage. Le " +
            "pipeline ne fait la difference sur aucune des trois, et c'est ce qui rend les tests hors ligne " +
            "possibles. Le descripteur de modele est expose parce que sans lui, l'incoherence entre le " +
            "modele courant et celui qui a construit l'index serait indetectable."),

        (typeof(IVectorIndex),
            "Un magasin qui rend des fragments du Domain deja scores, pas des lignes a rehydrater. Sa " +
            "signature laisse volontairement entrer une notion metier — le controle d'acces, via " +
            "SearchFilter : c'est le dilemme central du cours, pose a decouvert plutot que masque."),

        (typeof(ILanguageModel),
            "Le port le plus important, et le plus etroit : une chaine entre, une chaine sort. Pas de " +
            "conversation, pas d'agents, pas de memoire. L'etroitesse est la protection : ce que le modele " +
            "rend est une proposition, analysee puis jugee par la politique du Domain. Toutes les regles de " +
            "securite passent APRES cette frontiere, jamais dedans."),

        (typeof(IPromptCatalog),
            "Un prompt gouverne le comportement du systeme autant que du code, tout en se modifiant sans " +
            "recompilation. En faire un artefact charge, nomme, versionne et empreinte permet de le relire " +
            "sans lire le code, de comparer deux versions sur le meme jeu de questions, et de faire entrer " +
            "son empreinte dans les instantanes."),

        (typeof(IClock),
            "Le plus petit port du projet, et c'est ce qui en fait un bon exemple. DateTimeOffset.UtcNow " +
            "est une entree-sortie deguisee : sans cette frontiere, deux instantanes du meme jeu de " +
            "questions differeraient toujours par leur date, et la comparaison — dont c'est le seul objet — " +
            "serait inutilisable en test."),

        (typeof(ISnapshotStore),
            "L'Application sait qu'un instantane se range et se relit par son nom ; qu'il finisse en JSON " +
            "indente dans snapshots/ ne la regarde pas. Aucun attribut de serialisation ne remonte dans ses " +
            "types, sinon la couche dependrait du format retenu pour l'ecriture."),
    };

    /// <summary>Affiche les huit ports, leur adaptateur actif et la justification de la frontiere.</summary>
    public static Task<int> RunAsync(
        CommandLine commandLine,
        ConsoleRenderer renderer,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        commandLine.EnsureKnownOptions(CliConfiguration.OverrideOptions);

        var options = CliConfiguration.Load(commandLine);
        using var host = CliHost.Create(options);

        renderer.Title("Les huit ports");
        renderer.Note($"Profil « {options.Profile} » — la colonne des adaptateurs change avec lui, le reste non.");

        renderer.Line();
        renderer.Table(
            new[] { "Port", "Adaptateur actif" },
            Ports.Select(port => (IReadOnlyList<string>)new[]
            {
                port.Contract.Name,
                DescribeAdapter(host, port.Contract),
            }).ToList());

        foreach (var (contract, justification) in Ports)
        {
            renderer.Section(contract.Name);
            renderer.Paragraph(justification);
        }

        renderer.Line();
        renderer.Paragraph(
            "Huit frontieres, et pas une de plus. La strategie de recuperation (pre ou post-filtrage) n'en " +
            "est pas une : ses deux implementations vivent dans l'Application et ne parlent qu'a " +
            "IVectorIndex. Le contraste est volontaire — un port se justifie par ce qu'il rend remplacable, " +
            "pas par le fait qu'on puisse en ecrire une interface.");

        return Task.FromResult(ExitCodes.Success);
    }

    /// <summary>
    /// Le nom du type reellement branche. La resolution peut echouer — un catalogue de
    /// gabarits charge tout au demarrage et refuse un dossier absent — et cette commande
    /// doit rester lisible dans ce cas : elle sert justement a comprendre le montage.
    /// </summary>
    private static string DescribeAdapter(CliHost host, Type contract)
    {
        var instance = host.TryResolve(contract);

        return instance?.GetType().Name ?? "(non resolu — voir assistantqr doctor)";
    }
}
