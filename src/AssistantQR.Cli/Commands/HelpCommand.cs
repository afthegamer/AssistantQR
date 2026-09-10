using AssistantQR.Cli.Configuration;
using AssistantQR.Cli.Rendering;

namespace AssistantQR.Cli.Commands;

/// <summary>
/// <c>assistantqr help</c> : la syntaxe complete, sans aller lire le code.
/// </summary>
/// <remarks>
/// L'aide est ecrite a la main plutot que derivee d'attributs, pour la meme raison que le
/// dispatcher : elle doit rester lisible telle quelle dans un fichier, y compris par
/// quelqu'un qui ne fait pas tourner le programme.
/// </remarks>
internal static class HelpCommand
{
    /// <summary>Affiche l'aide generale.</summary>
    public static Task<int> RunAsync(ConsoleRenderer renderer)
    {
        renderer.Title("assistantqr — assistant de questions-reponses sur corpus, avec citations obligatoires");

        renderer.Paragraph(
            "Depot pedagogique : la meme application, montee de deux facons. En profil « offline » tout est " +
            "factice et deterministe, aucun reseau n'est sollicite ; en profil « local » les memes cas " +
            "d'usage parlent a Ollama et a un service Python. Aucune ligne du Domain ni des cas d'usage ne " +
            "change entre les deux.");

        renderer.Section("Commandes");
        renderer.Table(
            new[] { "Commande", "Ce qu'elle fait" },
            new List<IReadOnlyList<string>>
            {
                new[] { "doctor", "Sonde l'environnement : dossiers, service Python, Ollama, modele demande." },
                new[] { "corpus list", "Liste les documents du corpus avec leur niveau d'acces." },
                new[] { "index", "Reconstruit l'index vectoriel et affiche duree, morceaux, modele, decoupage." },
                new[] { "ask \"<question>\"", "Pose une question et affiche la reponse citee, ou le refus motive." },
                new[] { "snapshot record <nom>", "Rejoue un jeu de questions et archive le comportement observe." },
                new[] { "snapshot list", "Liste les instantanes enregistres avec leur configuration." },
                new[] { "snapshot compare <a> <b>", "Compare deux instantanes : ecarts cote a cote et taux de derive." },
                new[] { "demo llm-swap", "Scenario A : deux modeles de generation, une seule recuperation." },
                new[] { "demo embedding-swap", "Scenario B : reindexation avec un autre modele, derive mesuree." },
                new[] { "demo access-filter \"<q>\"", "Pre-filtrage contre post-filtrage sur la meme question." },
                new[] { "ports", "Les huit frontieres du systeme et l'adaptateur branche derriere chacune." },
                new[] { "help", "Cette page." },
            });

        renderer.Section("Options communes (elles surchargent le fichier et l'environnement)");
        renderer.Table(
            new[] { "Option", "Effet" },
            new List<IReadOnlyList<string>>
            {
                new[] { "--profile <offline|local>", "Choisit le montage : tout factice, ou services locaux." },
                new[] { "--embedding <nom>", "Modele d'embeddings (« hashing-fake », « bge-m3 »...)." },
                new[] { "--dimension <n>", "Dimension attendue des vecteurs." },
                new[] { "--chunking <id>", "paragraph, whole-document, fixed, fixed-<taille>-<recouvrement>." },
                new[] { "--llm <nom>", "Modele de generation (« extractive-fake », « replay », « granite4.2:3b »)." },
                new[] { "--filter <pre|post>", "Quand le controle d'acces s'applique." },
                new[] { "--topk <n>", "Nombre de morceaux demandes a l'index." },
                new[] { "--min-score <x>", "Seuil de similarite en dessous duquel un morceau est juge hors sujet." },
                new[] { "--temperature <x>", "Temperature du modele de generation." },
                new[] { "--seed <n>", "Graine transmise au modele quand il l'accepte." },
                new[] { "--prompt <nom>", "Nom du gabarit de prompt." },
                new[] { "--prompt-version <v>", "Version du gabarit (« 1.0.0 », « 1.1.0 »)." },
                new[] { "--strict", "Refuse d'interroger un index construit avec un autre modele." },
            });

        renderer.Section("Options propres a certaines commandes");
        renderer.Table(
            new[] { "Option", "Commandes", "Effet" },
            new List<IReadOnlyList<string>>
            {
                new[] { "--user <id>", "ask, demo access-filter", "Identifiant du demandeur." },
                new[]
                {
                    "--clearance <niveau>", "ask, demo access-filter",
                    "Habilitation : public, internal, confidential.",
                },
                new[] { "--trace", "ask", "Affiche la trace complete de recuperation." },
                new[] { "--questions <fichier>", "snapshot record, demo", "Jeu de questions a rejouer." },
                new[] { "--models <a,b>", "demo llm-swap, embedding-swap", "Les deux modeles a comparer." },
                new[] { "--dimensions <n,m>", "demo embedding-swap", "Les deux dimensions a comparer." },
            });

        renderer.Section("Configuration");
        renderer.Paragraph(
            $"Trois sources, de la plus generale a la plus specifique : le fichier " +
            $"{CliConfiguration.FileName} copie a cote du binaire, puis les variables d'environnement " +
            $"prefixees {CliConfiguration.EnvironmentPrefix}, puis les options ci-dessus. Le double " +
            "soulignement separe les niveaux dans une variable d'environnement.");

        renderer.Line();
        renderer.Bullet($"{CliConfiguration.EnvironmentPrefix}PROFILE=local");
        renderer.Bullet($"{CliConfiguration.EnvironmentPrefix}EMBEDDINGS__MODEL=bge-m3");
        renderer.Bullet($"{CliConfiguration.EnvironmentPrefix}PIPELINE__TOPK=8");

        renderer.Section("Pour commencer");
        renderer.Bullet("assistantqr doctor");
        renderer.Bullet("assistantqr corpus list");
        renderer.Bullet("assistantqr ask \"Quels sont les horaires d'ouverture le samedi ?\" --trace");
        renderer.Bullet("assistantqr demo access-filter \"Y a-t-il une amende si je rends un livre en retard ?\"");
        renderer.Bullet("assistantqr ports");

        renderer.Section("Codes de retour");
        renderer.Table(
            new[] { "Code", "Signification" },
            new List<IReadOnlyList<string>>
            {
                new[] { "0", "Succes. Un refus de repondre en fait partie : c'est une decision, pas une panne." },
                new[] { "1", "Erreur d'usage : la commande tapee n'a pas de sens." },
                new[] { "2", "Erreur d'execution : service injoignable, dossier absent, reglage incoherent." },
            });

        return Task.FromResult(ExitCodes.Success);
    }
}
