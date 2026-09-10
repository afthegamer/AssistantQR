using System.Globalization;

using AssistantQR.Cli.Configuration;
using AssistantQR.Cli.Rendering;

using AssistantQR.Application.Configuration;
using AssistantQR.Application.Model;
using AssistantQR.Application.Ports;
using AssistantQR.Application.Retrieval;
using AssistantQR.Application.UseCases.AnswerQuestion;

using AssistantQR.Domain.Access;
using AssistantQR.Domain.Questions;

using Microsoft.Extensions.DependencyInjection;

namespace AssistantQR.Cli.Commands;

/// <summary>
/// <c>assistantqr demo access-filter</c> : la meme question, filtree avant puis apres la
/// recherche.
/// </summary>
/// <remarks>
/// LE DILEMME, RENDU MESURABLE. En PRE-filtrage, l'index ne classe que ce que le demandeur
/// a le droit de lire : le topK est entierement utile, la pertinence est meilleure — au
/// prix d'une regle metier (le controle d'acces) desormais dupliquee dans le contrat d'un
/// composant technique, donc hors du Domain, donc hors de portee des tests qui la
/// protegent. En POST-filtrage, l'index ignore tout des habilitations et la regle reste a
/// un seul endroit — au prix d'un topK qui se remplit de documents que l'on retirera
/// ensuite, et donc de reponses parfois plus pauvres pour un usager peu habilite.
///
/// POURQUOI DEUX VALEURS DE topK PLUTOT QU'UNE. Le dilemme a deux formes, et la plus forte
/// ne se montre pas toujours au topK configure. Forme FORTE : les deux modes ne livrent pas
/// le meme nombre d'extraits, parce qu'un document interdit a consomme une place du topK
/// qui est ensuite perdue. Forme FAIBLE, et bien plus trompeuse : les extraits livres
/// coincident, la divergence n'existe que dans le classement intermediaire, et une lecture
/// de la seule reponse conclurait a tort que le choix du mode est sans consequence.
/// N'afficher que la seconde laisserait croire que la premiere est theorique ; la commande
/// cherche donc un topK qui exhibe la premiere, et montre les deux.
///
/// Aucun des deux modes n'est « le bon ». Le depot laisse les deux disponibles et cette
/// commande affiche ce que chacun laisse passer, pour que l'arbitrage se fasse sur des
/// listes plutot que sur des convictions.
/// </remarks>
internal static class AccessFilterDemo
{
    /// <summary>Options propres a la demonstration.</summary>
    public static readonly string[] OwnOptions = { "user", "clearance" };

    /// <summary>Rejoue la question dans les deux modes, a un ou deux topK, et affiche les ensembles.</summary>
    public static async Task<int> RunAsync(
        CommandLine commandLine,
        ConsoleRenderer renderer,
        CancellationToken cancellationToken)
    {
        commandLine.EnsureKnownOptions(CliConfiguration.OverrideOptions.Concat(OwnOptions).ToArray());

        var text = commandLine.Positional(2)
                   ?? throw new UsageException(
                       "Il manque la question. Exemple : assistantqr demo access-filter " +
                       "\"Y a-t-il une amende si je rends un livre en retard ?\" --clearance internal");

        var question = Question.From(text);
        var requester = AskCommand.ParseRequester(commandLine);

        // « --topk N » n'est pas seulement transmis a la configuration : sa PRESENCE change
        // la forme de la demonstration. Sans elle, la commande choisit elle-meme un second
        // topK pour exhiber la forme forte du dilemme ; avec elle, l'utilisateur a demande
        // un point de mesure precis et on ne lui en impose pas un autre.
        var explicitTopK = commandLine.Integer("topk");

        var baseOptions = CliConfiguration.Load(commandLine);
        var configuredTopK = baseOptions.Pipeline.TopK;

        renderer.Title("Pre-filtrage contre post-filtrage");
        renderer.Pairs(new[]
        {
            ("Question", question.Text),
            ("Demandeur", $"{requester.Id} (habilitation « {requester.Clearance.Name} »)"),
            ("topK", explicitTopK.HasValue
                ? $"{Count(configuredTopK)} (impose par --topk)"
                : $"{Count(configuredTopK)} (configure)"),
            ("Seuil de score", baseOptions.Pipeline.MinScore.ToString("0.###", CultureInfo.InvariantCulture)),
        });

        renderer.Line();

        // Un seul index et un seul service d'embeddings pour toute la commande : les quatre
        // executions qui suivent interrogent litteralement les memes vecteurs, et l'ecart
        // observe ne peut donc venir que de la strategie et du topK.
        using var indexOwner = CliHost.Create(baseOptions);
        await CliHost.EnsureIndexAsync(indexOwner, renderer, cancellationToken).ConfigureAwait(false);

        var sharedIndex = indexOwner.Service<IVectorIndex>();
        var sharedEmbeddings = indexOwner.Service<IEmbeddingService>();

        var command = new AnswerQuestionCommand(question, requester);
        var plan = new List<(string Label, int TopK)>();

        if (explicitTopK.HasValue)
        {
            plan.Add(("CAS UNIQUE", configuredTopK));
        }
        else
        {
            // La sonde n'appelle PAS le modele de langue : elle ne consulte que les deux
            // strategies de recuperation. Chercher un topK divergeant ne doit pas couter
            // une generation par essai, surtout en profil « local » ou chacune est lente.
            var divergentTopK = await FindDivergentTopKAsync(
                sharedIndex,
                sharedEmbeddings,
                baseOptions.ToPipelineOptions(),
                question,
                requester,
                configuredTopK,
                cancellationToken).ConfigureAwait(false);

            if (divergentTopK is { } reduced && reduced < configuredTopK)
            {
                plan.Add(("CAS 1", reduced));
                plan.Add(("CAS 2", configuredTopK));
            }
            else
            {
                RenderMissingStrongCase(renderer, divergentTopK, configuredTopK);
                plan.Add(("CAS UNIQUE", configuredTopK));
            }
        }

        foreach (var (label, topK) in plan)
        {
            var (pre, post) = await RunBothModesAsync(
                commandLine, topK, sharedIndex, sharedEmbeddings, command, cancellationToken)
                .ConfigureAwait(false);

            RenderCase(renderer, label, topK, pre, post);
        }

        RenderArbitration(renderer);

        return ExitCodes.Success;
    }

    /// <summary>
    /// Cherche, en descendant depuis le topK configure, la premiere valeur pour laquelle
    /// les deux modes ne livrent pas le meme ENSEMBLE d'extraits au modele.
    /// </summary>
    /// <remarks>
    /// POURQUOI CHERCHER PLUTOT QUE CODER UNE VALEUR EN DUR. Le topK qui fait diverger
    /// depend de la question, du corpus, du decoupage, du modele d'embeddings et du seuil
    /// de score : c'est un resultat de mesure, pas une constante. Un « 3 » ecrit dans le
    /// code se serait tu le jour ou l'un de ces cinq reglages change, et la demonstration
    /// aurait affirme une divergence sans la montrer. On la cherche donc a chaque
    /// execution, et on avoue quand on ne la trouve pas.
    ///
    /// La condition d'apparition est precise : il faut qu'un document NON LISIBLE par le
    /// demandeur se classe DANS le topK. Le post-filtrage depense alors une place pour lui,
    /// puis la retire sans la remplacer — le topK est ampute. Le pre-filtrage, lui, ne l'a
    /// jamais classe et rend la place a un extrait lisible.
    /// </remarks>
    private static async Task<int?> FindDivergentTopKAsync(
        IVectorIndex index,
        IEmbeddingService embeddings,
        PipelineOptions template,
        Question question,
        Requester requester,
        int fromTopK,
        CancellationToken cancellationToken)
    {
        var query = await embeddings.EmbedQueryAsync(question.Text, cancellationToken).ConfigureAwait(false);

        var pre = RetrievalStrategyFactory.Create(AccessFilterMode.Pre, index);
        var post = RetrievalStrategyFactory.Create(AccessFilterMode.Post, index);

        for (var topK = fromTopK; topK >= 1; topK--)
        {
            var options = template with { TopK = topK };

            var preOutcome = await pre.RetrieveAsync(query, requester, options, cancellationToken)
                .ConfigureAwait(false);
            var postOutcome = await post.RetrieveAsync(query, requester, options, cancellationToken)
                .ConfigureAwait(false);

            if (DeliveredSetsDiffer(preOutcome.AfterAccessFilter, postOutcome.AfterAccessFilter))
            {
                return topK;
            }
        }

        return null;
    }

    /// <summary>Rejoue la question dans les deux modes, a un topK donne, sur l'index partage.</summary>
    private static async Task<(AnswerQuestionResult Pre, AnswerQuestionResult Post)> RunBothModesAsync(
        CommandLine commandLine,
        int topK,
        IVectorIndex index,
        IEmbeddingService embeddings,
        AnswerQuestionCommand command,
        CancellationToken cancellationToken)
    {
        var optionsPre = CliConfiguration.Load(commandLine);
        optionsPre.Pipeline.AccessFilterMode = "pre";
        optionsPre.Pipeline.TopK = topK;

        var optionsPost = CliConfiguration.Load(commandLine);
        optionsPost.Pipeline.AccessFilterMode = "post";
        optionsPost.Pipeline.TopK = topK;

        void Share(IServiceCollection services)
        {
            services.AddSingleton<IVectorIndex>(index);
            services.AddSingleton<IEmbeddingService>(embeddings);
        }

        using var hostPre = CliHost.Create(optionsPre, Share);
        using var hostPost = CliHost.Create(optionsPost, Share);

        var pre = await hostPre.Service<AnswerQuestionUseCase>()
            .ExecuteAsync(command, cancellationToken).ConfigureAwait(false);

        var post = await hostPost.Service<AnswerQuestionUseCase>()
            .ExecuteAsync(command, cancellationToken).ConfigureAwait(false);

        return (pre, post);
    }

    /// <summary>Affiche un cas complet : les deux colonnes, les compteurs, la lecture.</summary>
    private static void RenderCase(
        ConsoleRenderer renderer,
        string label,
        int topK,
        AnswerQuestionResult pre,
        AnswerQuestionResult post)
    {
        // Un extrait peut disparaitre entre l'index et le modele pour deux raisons sans
        // rapport — score trop bas, ou habilitation insuffisante. Les confondre dans une
        // demonstration sur le controle d'acces serait la pire des approximations.
        var droppedPre = AskCommand.SplitDropped(pre.Trace);
        var droppedPost = AskCommand.SplitDropped(post.Trace);

        var diverges = DeliveredSetsDiffer(pre.Trace.Supplied, post.Trace.Supplied);

        // Trois etats, pas deux : les listes different ; elles coincident alors qu'un document
        // interdit a bel et bien occupe le classement ; ou rien d'interdit n'est jamais entre.
        // Confondre les deux derniers ferait annoncer un piege la ou il n'y a rien a voir.
        var titledInvisible = !diverges && droppedPost.Forbidden.Count > 0;

        renderer.Section(
            $"{label}, topK = {Count(topK)} — " +
            (diverges
                ? "divergence des extraits livres"
                : titledInvisible ? "divergence invisible" : "aucune divergence"));

        renderer.Paragraph(diverges
            ? "Les deux modes ne livrent pas le meme ensemble d'extraits au modele : le dilemme se "
              + "lit directement dans la reponse."
            : titledInvisible
                ? "Les deux modes livrent le meme ensemble d'extraits au modele : le dilemme ne se lit "
                  + "que dans la trace. C'est le cas le plus trompeur."
                : "Les deux modes livrent le meme ensemble d'extraits et aucun document interdit n'a "
                  + "occupe le classement : a ce topK, le choix du mode ne se voit nulle part.");

        renderer.Line();
        renderer.SideBySide(
            "PRE-filtrage (l'index filtre)",
            "POST-filtrage (le Domain filtre)",
            new[]
            {
                (Block("Rendu par l'index", pre.Trace.FromIndex),
                    Block("Rendu par l'index", post.Trace.FromIndex)),
                (Block("Soumis au modele", pre.Trace.Supplied),
                    Block("Soumis au modele", post.Trace.Supplied)),
                (Block("Ecarte par le seuil de score", droppedPre.BelowThreshold),
                    Block("Ecarte par le seuil de score", droppedPost.BelowThreshold)),
                (Block("Ecarte par le controle d'acces", droppedPre.Forbidden),
                    Block("Ecarte par le controle d'acces", droppedPost.Forbidden)),
                (Outcome(pre), Outcome(post)),
            });

        renderer.Bullet(
            $"Pre-filtrage : {Count(pre.Trace.CandidatesFromIndex)} candidat(s) rendus, tous deja lisibles ; " +
            $"le topK n'a ete gaspille pour personne, {Count(pre.Trace.CandidatesAfterAccessFilter)} " +
            "extrait(s) atteignent le modele.");

        renderer.Bullet(
            $"Post-filtrage : {Count(post.Trace.CandidatesFromIndex)} candidat(s) rendus dont " +
            $"{Count(droppedPost.Forbidden.Count)} retire(s) par le controle d'acces apres coup ; " +
            $"{Count(post.Trace.CandidatesAfterAccessFilter)} extrait(s) atteignent le modele.");

        renderer.Line();
        renderer.Paragraph(DescribeDivergence(pre, post, droppedPost.Forbidden));
    }

    /// <summary>
    /// Dit franchement qu'on n'a pas trouve de topK exhibant la forme forte, et a quelle
    /// condition elle apparait.
    /// </summary>
    private static void RenderMissingStrongCase(ConsoleRenderer renderer, int? divergentTopK, int configuredTopK)
    {
        if (divergentTopK is null)
        {
            renderer.Section("Forme forte du dilemme : introuvable sur cette question");
            renderer.Paragraph(
                $"Aucune valeur de topK entre 1 et {Count(configuredTopK)} ne fait livrer au modele deux " +
                "ensembles d'extraits differents. Autant le dire plutot que de mettre en scene un " +
                "dilemme absent. " +
                "La condition d'apparition est precise : il faut qu'un document que le demandeur n'a PAS " +
                "le droit de lire se classe DANS le topK. Alors le post-filtrage depense une place pour " +
                "lui, la retire ensuite sans la remplacer, et livre moins d'extraits que le pre-filtrage. " +
                "Ici, soit aucun document interdit n'entre dans le classement, soit le seuil de score le " +
                "retirait de toute facon. Essaie une question dont le vocabulaire est partage entre " +
                "plusieurs niveaux — les retards, le budget, le planning des agents — avec une " +
                "habilitation intermediaire.");
            return;
        }

        renderer.Section("Forme forte du dilemme : deja visible au topK configure");
        renderer.Paragraph(
            $"Inutile de reduire le topK pour l'exhiber : des {Count(configuredTopK)}, les deux modes ne " +
            "livrent deja pas le meme ensemble d'extraits. Il n'y a donc pas ici de second cas a montrer, " +
            "celui ou les listes coincident et ou l'ecart ne se lit que dans le classement — c'est " +
            "pourtant le plus trompeur des deux. Augmente le topK avec --topk pour le retrouver.");
    }

    /// <summary>Le cout de chaque mode, nomme sans etre arbitre.</summary>
    private static void RenderArbitration(ConsoleRenderer renderer)
    {
        renderer.Line();
        renderer.Paragraph(
            "Le cout du pre-filtrage n'apparait pas dans ces listes : il est structurel. Pour filtrer, " +
            "l'index doit connaitre les niveaux d'acces, donc une regle metier vit desormais dans un " +
            "composant technique — dupliquee, hors du Domain, et hors d'atteinte des tests qui la " +
            "protegent. C'est pour cela que la defense en profondeur reste active dans les deux modes : " +
            "AccessPolicy repasse sur les resultats du pre-filtrage, ou elle ne doit rien trouver a retirer.");

        renderer.Line();
        renderer.Paragraph(
            "Chacun paie donc, et la commande n'a pas a choisir a ta place : le pre-filtrage garantit un " +
            "topK plein — chaque place sert un extrait que le demandeur a le droit de lire — mais fait " +
            "vivre le controle d'acces dans l'index ; le post-filtrage garde la regle dans le Domain, la " +
            "ou elle se teste sans cle d'API, mais gaspille des places du topK et laisse circuler dans le " +
            "processus des documents interdits, que seul un filtre pose au bon endroit empeche d'atteindre " +
            "le prompt — le dernier rempart etant AnswerPolicy, qui refuse une reponse citant un document " +
            "que le demandeur n'avait pas le droit de lire.");
    }

    private static string Block(string title, IReadOnlyList<ScoredFragment> fragments)
    {
        if (fragments.Count == 0)
        {
            return title + " : (aucun)";
        }

        var lines = fragments.Select(scored =>
            $"{ConsoleRenderer.Score(scored.Score)}  {AskCommand.FragmentKey(scored)} " +
            $"({scored.Fragment.AccessLevel.Name})");

        return title + " :\n" + string.Join("\n", lines);
    }

    private static string Outcome(AnswerQuestionResult result) =>
        "Issue : " + AskCommand.SummariseOutcome(result.Outcome);

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Les deux modes livrent-ils au modele des ensembles d'extraits reellement differents ?
    /// </summary>
    /// <remarks>
    /// La comparaison porte sur des ENSEMBLES et non sur des sequences : c'est la seule
    /// formulation qui corresponde a la these du cours (« ce ne sont pas des sous-ensembles
    /// l'un de l'autre »). Un simple reordonnancement ne serait pas une divergence des
    /// extraits livres, et la sonde qui choisit le topK doit appliquer exactement le meme
    /// critere que le texte qui commente le resultat — sinon la commande annoncerait un cas
    /// que son propre commentaire contredirait.
    /// </remarks>
    private static bool DeliveredSetsDiffer(
        IReadOnlyList<ScoredFragment> left,
        IReadOnlyList<ScoredFragment> right)
    {
        var leftKeys = left.Select(AskCommand.FragmentKey).ToHashSet(StringComparer.Ordinal);
        return !leftKeys.SetEquals(right.Select(AskCommand.FragmentKey));
    }

    /// <remarks>
    /// Ce commentaire est deduit des listes, jamais suppose. Il existe un cas piegeux qu'une
    /// simple comparaison des extraits fournis ferait passer pour un non-evenement : les deux
    /// modes livrent la meme chose au modele, alors qu'un document interdit occupait bel et
    /// bien une place du topK en post-filtrage. La place liberee par le pre-filtrage est
    /// alors revenue a un extrait lisible mais trop faible, elimine ensuite par le seuil.
    /// La divergence est reelle dans le classement et invisible dans la reponse : c'est
    /// precisement le genre d'ecart qu'une demonstration doit nommer plutot que taire.
    /// </remarks>
    private static string DescribeDivergence(
        AnswerQuestionResult pre,
        AnswerQuestionResult post,
        IReadOnlyList<ScoredFragment> forbiddenInPost)
    {
        var preKeys = pre.Trace.Supplied.Select(AskCommand.FragmentKey).ToList();
        var postKeys = post.Trace.Supplied.Select(AskCommand.FragmentKey).ToList();

        if (!DeliveredSetsDiffer(pre.Trace.Supplied, post.Trace.Supplied))
        {
            if (forbiddenInPost.Count == 0)
            {
                return "Sur cette question, les deux modes fournissent exactement les memes extraits : rien " +
                       "d'interdit n'occupait le classement, le choix du mode n'a donc aucune consequence " +
                       "observable. Essaie une question dont le vocabulaire est partage entre plusieurs " +
                       "niveaux — les retards, le budget, le planning des agents — avec une habilitation " +
                       "intermediaire.";
            }

            var reclaimed = pre.Trace.FromIndex.Select(AskCommand.FragmentKey)
                .Except(post.Trace.FromIndex.Select(AskCommand.FragmentKey), StringComparer.Ordinal)
                .ToList();

            return "Les deux modes finissent par livrer les memes extraits au modele, mais ils n'y sont " +
                   "pas arrives de la meme facon. En post-filtrage, " +
                   string.Join(", ", forbiddenInPost.Select(AskCommand.FragmentKey)) +
                   " occupait une place du topK avant d'en etre retire par le controle d'acces. " +
                   (reclaimed.Count == 0
                       ? "En pre-filtrage, cette place est restee vide, faute de candidat lisible en reserve. "
                       : $"En pre-filtrage, cette place est revenue a {string.Join(", ", reclaimed)}, que le " +
                         "seuil de score a ensuite ecarte. ") +
                   "La divergence est donc bien reelle — elle est dans le classement — et pourtant " +
                   "invisible dans la reponse. C'est le cas le plus trompeur des deux, et celui qui " +
                   "justifie de lire la trace plutot que la seule sortie.";
        }

        var onlyPre = preKeys.Except(postKeys, StringComparer.Ordinal).ToList();
        var onlyPost = postKeys.Except(preKeys, StringComparer.Ordinal).ToList();

        // La place perdue est le coeur de la forme forte : la nommer en chiffres evite que le
        // lecteur ait a recompter deux colonnes pour constater l'amputation.
        var slotLoss = preKeys.Count > postKeys.Count && forbiddenInPost.Count > 0
            ? $"Le compte le dit sans detour : {Count(postKeys.Count)} extrait(s) livre(s) en " +
              $"post-filtrage contre {Count(preKeys.Count)} en pre-filtrage. " +
              string.Join(", ", forbiddenInPost.Select(AskCommand.FragmentKey)) +
              (forbiddenInPost.Count == 1
                  ? " a consomme une place du topK avant d'en etre retire, et cette place est perdue : " +
                    "personne ne la reprend, "
                  : $" ont consomme {Count(forbiddenInPost.Count)} places du topK avant d'en etre retires, " +
                    "et ces places sont perdues : personne ne les reprend, ") +
              "le modele travaille avec moins de matiere. "
            : string.Empty;

        return "Les deux modes divergent. " +
               slotLoss +
               (onlyPre.Count == 0
                   ? string.Empty
                   : $"Le pre-filtrage a fait remonter {string.Join(", ", onlyPre)}, que le post-filtrage " +
                     "n'a jamais eu l'occasion de considerer : ces extraits etaient trop bas dans le " +
                     "classement general pour entrer dans le topK. ") +
               (onlyPost.Count == 0
                   ? string.Empty
                   : $"Le post-filtrage a conserve {string.Join(", ", onlyPost)}, absents du pre-filtrage. ") +
               "C'est exactement l'arbitrage : le pre-filtrage rend au demandeur un topK entierement " +
               "utile, le post-filtrage lui rend un topK ampute de ce qu'il n'avait pas le droit de voir.";
    }
}
