using System.Globalization;

using AssistantQR.Cli.Configuration;
using AssistantQR.Cli.Rendering;

using AssistantQR.Application.Model;
using AssistantQR.Application.Ports;
using AssistantQR.Application.UseCases.AnswerQuestion;
using AssistantQR.Application.UseCases.IndexCorpus;
using AssistantQR.Application.UseCases.Snapshots;

using AssistantQR.Domain.Access;
using AssistantQR.Domain.Questions;

using AssistantQR.Infrastructure.Configuration;

using Microsoft.Extensions.DependencyInjection;

namespace AssistantQR.Cli.Commands;

/// <summary>
/// SCENARIO B — <c>assistantqr demo embedding-swap</c> : reindexer avec un autre modele
/// d'embeddings, et mesurer ce que cela deplace.
/// </summary>
/// <remarks>
/// LA PANNE QUE CE SCENARIO MET EN SCENE N'EN EST PAS UNE, AU SENS OU RIEN NE TOMBE. On
/// remplace le modele qui transforme le texte en vecteurs, on reconstruit l'index, tout
/// repond, aucun test ne casse — et une partie des reponses n'est plus la meme. Aucun
/// mecanisme du systeme ne signale l'evenement, parce qu'aucun mecanisme n'a de raison de
/// le faire : la substitution est legale. Seule une mesure faite exprES, avant et apres,
/// la rend visible. C'est tout l'objet des instantanes.
///
/// POURQUOI LA DEMONSTRATION HORS LIGNE TIENT A DIMENSION EGALE.
/// L'Infrastructure fournit DEUX faux d'embeddings : « hashing-fake » et
/// « hashing-fake-b ». Meme algorithme, meme dimension, meme determinisme — seule la
/// graine de projection change, donc la coordonnee sur laquelle chaque mot atterrit. Les
/// deux produisent des vecteurs egalement plausibles et totalement differents. C'est
/// exactement la situation de deux vrais modeles concurrents en dimension 1024 : rien ne
/// plante, l'index accepte les vecteurs, les tests d'integration passent, et le systeme
/// repond autre chose. Le passage de l'un a l'autre est UNE LIGNE de configuration
/// (<c>AssistantQR:Embeddings:Model</c>), ce qui est le geste que le cours projette.
///
/// LE CAS BRUYANT RESTE ACCESSIBLE, MAIS N'EST PLUS LE DEFAUT. Avec
/// « --dimensions 1024,384 », les deux modeles ne parlent plus le meme espace et l'index
/// proteste des la premiere requete perimee. C'est le cas confortable — celui qu'on
/// remarque — et il n'apprend presque rien : ce que ce depot doit rendre visible est la
/// panne SILENCIEUSE, celle ou personne ne proteste.
///
/// CE QUE CETTE DEMONSTRATION NE PEUT PAS FAIRE EN PROFIL « LOCAL », ET POURQUOI ELLE
/// REFUSE DE FAIRE SEMBLANT. Hors ligne, les deux embeddings sont calcules dans CE
/// processus : changer <c>AssistantQR:Embeddings:Model</c> change reellement les vecteurs.
/// En profil « local », non. Les vecteurs viennent du service Python, et <c>POST /embed</c>
/// ne transporte aucun nom de modele — seulement les textes et leur nature (voir
/// <c>python/embeddings_service/app/models.py</c>, classe <c>EmbedRequest</c>). Le modele
/// est resolu une seule fois, au demarrage du service, depuis <c>EMBEDDING_MODEL</c>.
/// Changer le nom du modele du cote C# ne deplacerait donc que l'ETIQUETTE — celle qui part
/// dans <c>POST /index/reset</c>, finit dans les metadonnees de l'index et dans l'empreinte
/// de configuration des instantanes — pendant que les vecteurs resteraient ceux du modele
/// deja charge. La commande afficherait une derive de 0 % en pretendant avoir change de
/// modele, ou etiquetterait d'un nom un index construit par un autre : la panne muette
/// exacte que ce scenario est cense rendre visible. Elle est donc refusee, avec un code de
/// retour non nul et la marche a suivre reelle (redemarrer le service, reindexer, comparer
/// deux instantanes enregistres de part et d'autre).
/// </remarks>
internal static class EmbeddingSwapDemo
{
    /// <summary>Nom de l'instantane pris avant la substitution.</summary>
    public const string BaselineName = "demo-embedding-avant";

    /// <summary>Nom de l'instantane pris apres la substitution.</summary>
    public const string CandidateName = "demo-embedding-apres";

    /// <summary>Options propres a la demonstration.</summary>
    public static readonly string[] OwnOptions = { "questions", "models", "dimensions" };

    /// <summary>Indexe, enregistre, reindexe avec un autre modele, enregistre, compare.</summary>
    public static async Task<int> RunAsync(
        CommandLine commandLine,
        ConsoleRenderer renderer,
        CancellationToken cancellationToken)
    {
        commandLine.EnsureKnownOptions(CliConfiguration.OverrideOptions.Concat(OwnOptions).ToArray());

        var reference = CliConfiguration.Load(commandLine);
        var (firstModel, secondModel) = commandLine.Pair("models")
                                        ?? (reference.Embeddings.Model, Counterpart(reference.Embeddings.Model));

        var (firstDimension, secondDimension) = ResolveDimensions(commandLine, reference);

        // Le refus tombe AVANT toute indexation : une demonstration qui ne peut pas dire la
        // verite ne doit pas commencer par reconstruire l'index de quelqu'un.
        if (!reference.IsOffline && !SameModel(firstModel, secondModel) &&
            !(IsHashingFake(firstModel) && IsHashingFake(secondModel)))
        {
            RefuseLocalSwap(renderer, firstModel, secondModel, firstDimension, secondDimension);
            return ExitCodes.Execution;
        }

        var optionsFirst = CliConfiguration.Load(commandLine);
        optionsFirst.Embeddings.Model = firstModel;
        optionsFirst.Embeddings.Dimension = firstDimension;

        var optionsSecond = CliConfiguration.Load(commandLine);
        optionsSecond.Embeddings.Model = secondModel;
        optionsSecond.Embeddings.Dimension = secondDimension;

        var questions = await QuestionSet
            .LoadAsync(reference, commandLine.Value("questions"), cancellationToken)
            .ConfigureAwait(false);

        renderer.Title("Scenario B — changer de modele d'embeddings");
        renderer.Pairs(new[]
        {
            ("Avant", $"{firstModel} (dimension {Number(firstDimension)})"),
            ("Apres", $"{secondModel} (dimension {Number(secondDimension)})"),
            ("Questions", Number(questions.Count)),
            ("Profil", reference.Profile),
            ("Instantanes", $"{BaselineName} puis {CandidateName}, dans " +
                            PathResolver.Resolve(reference.SnapshotsDirectory)),
        });

        renderer.Line();
        renderer.Paragraph(firstDimension == secondDimension
            ? "Les deux modeles produisent des vecteurs de MEME dimension. Rien ne peut donc " +
              "planter : l'index acceptera les vecteurs de l'un comme de l'autre, et c'est " +
              "precisement ce qui rend la substitution invisible."
            : $"Dimensions differentes demandees ({Number(firstDimension)} contre " +
              $"{Number(secondDimension)}) : c'est le detour bruyant, le cas confortable et le " +
              "moins interessant. Retire « --dimensions » pour voir la panne silencieuse.");

        using var hostFirst = CliHost.Create(optionsFirst);

        // Les deux montages ecrivent dans le MEME index : c'est ce qui fait de la seconde
        // etape une reindexation, et non la construction d'un second index parallele.
        var sharedIndex = hostFirst.Service<IVectorIndex>();

        using var hostSecond = CliHost.Create(optionsSecond, services =>
            services.AddSingleton<IVectorIndex>(sharedIndex));

        renderer.Section("1. Indexation avec le premier modele");
        var firstIndexing = await Reindex(hostFirst, renderer, cancellationToken).ConfigureAwait(false);

        renderer.Section("2. Instantane de reference");
        await Record(hostFirst, BaselineName, questions, renderer, cancellationToken).ConfigureAwait(false);

        renderer.Section("3. Reindexation avec le second modele");
        var secondIndexing = await Reindex(hostSecond, renderer, cancellationToken).ConfigureAwait(false);

        renderer.Section("4. Instantane candidat");
        await Record(hostSecond, CandidateName, questions, renderer, cancellationToken).ConfigureAwait(false);

        var comparison = await hostSecond.Service<CompareSnapshotsUseCase>()
            .ExecuteAsync(BaselineName, CandidateName, cancellationToken)
            .ConfigureAwait(false);

        renderer.Section("5. Cout de la substitution");
        renderer.Pairs(new[]
        {
            ("Reindexation avant", ConsoleRenderer.Duration(firstIndexing.Duration)),
            ("Reindexation apres", ConsoleRenderer.Duration(secondIndexing.Duration)),
            ("Dont embeddings (avant)", ConsoleRenderer.Duration(firstIndexing.EmbeddingDuration)),
            ("Dont embeddings (apres)", ConsoleRenderer.Duration(secondIndexing.EmbeddingDuration)),
            ("Taux de derive", ConsoleRenderer.Percent(comparison.DriftRatio)),
        });

        SnapshotCommand.RenderComparison(renderer, comparison);

        await RenderGuardRail(
            commandLine, sharedIndex, hostSecond, questions, firstModel, secondModel,
            firstDimension, secondDimension, renderer, cancellationToken).ConfigureAwait(false);

        renderer.Section("Conclusion");
        renderer.Paragraph(
            $"Aucune erreur. Aucun test casse. {Number(comparison.ChangedCount)} reponses sur " +
            $"{Number(comparison.TotalCount)} ont change.");

        renderer.Line();
        renderer.Paragraph(
            "Rien dans le systeme n'a signale l'evenement, et rien n'aurait pu : remplacer un modele " +
            "d'embeddings est une operation legitime. Ce qui rend la derive imputable n'est pas un " +
            "garde-fou, c'est l'empreinte de configuration enregistree a cote des reponses.");

        if (comparison.ChangedCount == 0)
        {
            renderer.Line();
            renderer.Paragraph(
                "Derive nulle ici : les deux passages ont produit exactement les memes vecteurs. C'est le " +
                "cas quand on compare un faux deterministe a lui-meme — « --models hashing-fake,hashing-fake » " +
                "par exemple. Sans « --models », la demonstration oppose « hashing-fake » a " +
                "« hashing-fake-b », deux projections differentes de meme dimension, et la mesure parle.");
        }

        return ExitCodes.Success;
    }

    /// <summary>
    /// Le modele oppose a celui qui est configure. Les deux faux se repondent l'un a
    /// l'autre : c'est ce qui donne, sans le moindre argument de ligne de commande, une
    /// substitution a dimension egale — la seule qui demontre quelque chose.
    /// </summary>
    private static string Counterpart(string model)
    {
        var trimmed = model.Trim();

        if (string.Equals(trimmed, EmbeddingOptions.HashingModel, StringComparison.OrdinalIgnoreCase))
        {
            return EmbeddingOptions.HashingModelB;
        }

        // Un vrai modele n'a pas de contrepartie evidente : on le compare a lui-meme et la
        // commande dira que la derive est nulle, plutot que d'inventer un nom de modele.
        return string.Equals(trimmed, EmbeddingOptions.HashingModelB, StringComparison.OrdinalIgnoreCase)
            ? EmbeddingOptions.HashingModel
            : model;
    }

    /// <summary>Deux noms de modele designent-ils le meme modele ?</summary>
    private static bool SameModel(string first, string second) =>
        string.Equals(first.Trim(), second.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Le nom designe-t-il l'une des deux doublures d'embeddings ?</summary>
    /// <remarks>
    /// Ces deux-la sont calculees DANS ce processus, y compris quand le profil est
    /// « local » (voir <c>UsesRemoteEmbeddings</c> dans la racine de composition : le
    /// service Python n'est pas sollicite quand le nom designe un faux). Leur nom commande
    /// donc reellement les vecteurs, et l'etiquette ne peut pas diverger du calcul :
    /// opposer l'une a l'autre ne ment pas. Le refus qui suit n'a pas a s'en meler, et il
    /// ne doit surtout pas leur servir une explication sur le service Python qui serait,
    /// pour le coup, fausse. C'est la racine de composition qui dira, le cas echeant,
    /// qu'une doublure n'a rien a faire en profil « local ».
    /// </remarks>
    private static bool IsHashingFake(string model) =>
        SameModel(model, EmbeddingOptions.HashingModel) || SameModel(model, EmbeddingOptions.HashingModelB);

    /// <summary>
    /// Le refus, en profil « local », de substituer un modele d'embeddings que ce processus
    /// ne choisit pas.
    /// </summary>
    /// <remarks>
    /// DEUX FOIS LE MEME NOM RESTE ACCEPTE. C'est un cas degenere — la derive attendue est
    /// de 0 % — mais il ne ment pas : l'etiquette et les vecteurs viennent du meme modele,
    /// et la commande le dit. Ce qui est refuse est uniquement la substitution, c'est-a-dire
    /// la seule situation ou l'etiquette et le calcul divergeraient.
    /// </remarks>
    private static void RefuseLocalSwap(
        ConsoleRenderer renderer,
        string firstModel,
        string secondModel,
        int firstDimension,
        int secondDimension)
    {
        renderer.Title("Scenario B — refuse en profil « local »");

        renderer.Paragraph(
            $"Substitution demandee : « {firstModel} » (dimension {Number(firstDimension)}) puis " +
            $"« {secondModel} » (dimension {Number(secondDimension)}). Elle n'aura pas lieu, et voici " +
            "pourquoi.");

        renderer.Line();
        renderer.Paragraph(
            "EN PROFIL « LOCAL », CE PROCESSUS NE CHOISIT PAS LE MODELE D'EMBEDDINGS. Les vecteurs sont " +
            "calcules par le service Python, et POST /embed ne transporte aucun nom de modele : son corps " +
            "n'a que « texts » et « kind » (python/embeddings_service/app/models.py, classe EmbedRequest). " +
            "Le modele est resolu une seule fois, au demarrage du service, depuis la variable " +
            "d'environnement EMBEDDING_MODEL.");

        renderer.Line();
        renderer.Paragraph(
            "Changer AssistantQR:Embeddings:Model ici ne deplacerait donc que l'ETIQUETTE — celle qui part " +
            "dans POST /index/reset, finit dans les metadonnees de l'index et dans l'empreinte de " +
            "configuration des instantanes — pendant que les vecteurs continueraient de venir de " +
            $"« {firstModel} ». La commande afficherait une derive de 0 % en pretendant avoir change de " +
            $"modele, ou pire, etiquetterait « {secondModel} » un index construit par « {firstModel} ». " +
            "C'est le mensonge silencieux exact que ce scenario est cense rendre visible : il est refuse, " +
            "pas produit.");

        renderer.Section("Comment mesurer reellement cette substitution");
        renderer.Paragraph(
            "Il faut redemarrer le service avec l'autre modele ENTRE les deux instantanes, et reindexer de " +
            "chaque cote. Ce processus ne peut pas redemarrer le service pour toi ; les commandes, dans " +
            "cet ordre :");

        renderer.Line();
        renderer.Line($"  1. powershell -File scripts/start-embeddings.ps1 -Model {firstModel}");
        renderer.Line($"  2. assistantqr index --profile local --embedding {firstModel} " +
                      $"--dimension {Number(firstDimension)}");
        renderer.Line($"  3. assistantqr snapshot record {BaselineName} --profile local " +
                      $"--embedding {firstModel} --dimension {Number(firstDimension)}");
        renderer.Line($"  4. powershell -File scripts/start-embeddings.ps1 -Model {secondModel}");
        renderer.Line($"  5. assistantqr index --profile local --embedding {secondModel} " +
                      $"--dimension {Number(secondDimension)}");
        renderer.Line($"  6. assistantqr snapshot record {CandidateName} --profile local " +
                      $"--embedding {secondModel} --dimension {Number(secondDimension)}");
        renderer.Line($"  7. assistantqr snapshot compare {BaselineName} {CandidateName}");

        renderer.Line();
        renderer.Paragraph(
            "Les etapes 2 et 5 sont indispensables : sans reindexation, l'etape 5 laisserait un index " +
            "peuple de vecteurs du premier modele sous le nom du second — le meme mensonge, deplace d'un " +
            "cran. L'etape 7 est la comparaison que « demo embedding-swap » automatise hors ligne ; ici " +
            "elle est faite a la main parce que l'etape « redemarrer le service » ne l'est pas.");

        renderer.Section("Le scenario reste jouable, et sans mentir");
        renderer.Paragraph(
            "En profil « offline », les deux embeddings sont calcules DANS ce processus : " +
            $"« {EmbeddingOptions.HashingModel} » et « {EmbeddingOptions.HashingModelB} », meme algorithme, " +
            "meme dimension, graines de projection differentes. Changer le nom change alors reellement les " +
            "vecteurs, et la derive mesuree est une vraie derive :");

        renderer.Line();
        renderer.Line("  assistantqr demo embedding-swap --profile offline");

        renderer.Line();
        renderer.Paragraph(
            "C'est la meme these — une ligne de configuration, aucune exception, aucun test rouge, des " +
            "reponses differentes — sur un couple de modeles dont ce processus a vraiment la maitrise.");

        renderer.Line();
        renderer.Failure(
            "Refus : en profil « local », « demo embedding-swap » ne peut pas changer de modele " +
            "d'embeddings sans mentir sur ce qu'il a mesure.");
    }

    /// <summary>
    /// Les deux dimensions sont EGALES par defaut : c'est la configuration qui rend la
    /// substitution silencieuse, donc celle que le cours doit montrer. « --dimensions »
    /// permet d'aller voir le cas bruyant, ou l'index refuse la requete.
    /// </summary>
    private static (int First, int Second) ResolveDimensions(CommandLine commandLine, AssistantOptions reference)
    {
        if (commandLine.Pair("dimensions") is { } pair)
        {
            return (ParseDimension(pair.First), ParseDimension(pair.Second));
        }

        var configured = reference.Embeddings.Dimension;

        return (configured, configured);
    }

    private static int ParseDimension(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dimension) && dimension > 0
            ? dimension
            : throw new UsageException(
                $"L'option « --dimensions » attend deux entiers positifs, or elle a recu « {value} ».");

    private static async Task<IndexCorpusResult> Reindex(
        CliHost host,
        ConsoleRenderer renderer,
        CancellationToken cancellationToken)
    {
        var result = await host.Service<IndexCorpusUseCase>().ExecuteAsync(cancellationToken).ConfigureAwait(false);

        renderer.Note(
            $"{Number(result.ChunkCount)} morceau(x) de {Number(result.DocumentCount)} document(s), " +
            $"modele « {result.EmbeddingModel} » en dimension {Number(result.Dimension)}, " +
            $"decoupage « {result.ChunkingStrategyId} », en {ConsoleRenderer.Duration(result.Duration)}.");

        return result;
    }

    private static async Task Record(
        CliHost host,
        string name,
        IReadOnlyList<QuestionSetItem> questions,
        ConsoleRenderer renderer,
        CancellationToken cancellationToken)
    {
        var snapshot = await host.Service<RecordSnapshotUseCase>()
            .ExecuteAsync(new RecordSnapshotCommand(name, questions), cancellationToken)
            .ConfigureAwait(false);

        var answered = snapshot.Entries.Count(entry => entry.Answered);

        renderer.Note(
            $"« {name} » : {Number(answered)} reponse(s), " +
            $"{Number(snapshot.Entries.Count - answered)} refus.");
        renderer.Note(snapshot.Configuration.ToString());
    }

    /// <summary>
    /// Le garde-fou, ou son absence : on interroge l'index reconstruit par le SECOND modele
    /// avec les vecteurs du PREMIER, sans rien reindexer. C'est la situation reelle quand
    /// quelqu'un change la configuration et oublie l'etape de reindexation.
    /// </summary>
    /// <remarks>
    /// A DIMENSION EGALE, RIEN NE S'Y OPPOSE. L'index recoit un vecteur de la bonne taille,
    /// calcule des cosinus parfaitement plausibles et rend un classement complet — sur des
    /// coordonnees qui ne veulent plus rien dire. Aucune exception, aucun test rouge, des
    /// scores d'apparence normale, et des documents sans rapport avec la question. La
    /// comparaison cote a cote est le seul moyen de le voir, et c'est bien le probleme.
    ///
    /// Le contraste avec <c>StrictIndexModelCheck</c> est la morale du scenario : le depot
    /// SAIT detecter l'incoherence — le cas d'usage compare le nom du modele a celui qui a
    /// construit l'index — mais ce controle est desactive par defaut. Les deux mondes sont
    /// exposes cote a cote pour que le choix soit vu comme un choix.
    /// </remarks>
    private static async Task RenderGuardRail(
        CommandLine commandLine,
        IVectorIndex sharedIndex,
        CliHost hostSecond,
        IReadOnlyList<QuestionSetItem> questions,
        string firstModel,
        string secondModel,
        int firstDimension,
        int secondDimension,
        ConsoleRenderer renderer,
        CancellationToken cancellationToken)
    {
        renderer.Section("6. Et si l'on oublie de reindexer ?");

        var item = questions[0];
        var command = new AnswerQuestionCommand(
            Question.From(item.Question),
            Requester.Create(item.UserId, item.Clearance));

        renderer.Paragraph(
            $"L'index est celui que « {secondModel} » vient de reconstruire. On pose la meme question " +
            $"deux fois : une fois avec le montage a jour, une fois avec un montage reste sur " +
            $"« {firstModel} » — la configuration a change, l'etape de reindexation a ete oubliee.");

        var fresh = await hostSecond.Service<AnswerQuestionUseCase>()
            .ExecuteAsync(command, cancellationToken)
            .ConfigureAwait(false);

        using var hostStale = CreateHost(commandLine, firstModel, firstDimension, strict: false, sharedIndex);

        AnswerQuestionResult? stale = null;
        Exception? refusal = null;

        try
        {
            stale = await hostStale.Service<AnswerQuestionUseCase>()
                .ExecuteAsync(command, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            refusal = exception;
        }

        if (stale is null)
        {
            renderer.Line();
            renderer.Paragraph($"L'index a refuse la requete : {refusal?.Message}");
            renderer.Line();
            renderer.Paragraph(
                $"La panne est ici BRUYANTE parce que les dimensions different ({Number(firstDimension)} " +
                $"contre {Number(secondDimension)}) : tu as demande « --dimensions ». C'est le cas " +
                "confortable, et le moins interessant. Relance sans cette option : a dimension egale, la " +
                "meme requete passe sans broncher, avec des scores plausibles et des resultats sans rapport.");
            return;
        }

        renderer.Line();
        renderer.Paragraph($"Question posee : « {item.Question} » (habilitation {item.Clearance}).");
        renderer.Line();

        renderer.SideBySide(
            $"Index a jour — questions {secondModel}",
            $"Index perime — questions {firstModel}",
            new[]
            {
                (Block("Rendu par l'index", fresh.Trace.FromIndex),
                    Block("Rendu par l'index", stale.Trace.FromIndex)),
                ("Issue : " + AskCommand.SummariseOutcome(fresh.Outcome),
                    "Issue : " + AskCommand.SummariseOutcome(stale.Outcome)),
            });

        renderer.Line();
        renderer.Paragraph(
            "AUCUNE EXCEPTION N'A ETE LEVEE. Les deux vecteurs ont la meme dimension, l'index a donc " +
            "accepte sans rien remarquer un vecteur produit par un autre modele, calcule ses cosinus et " +
            "rendu un classement complet. Les scores sont d'apparence normale ; les coordonnees, elles, " +
            "ne signifient plus la meme chose des deux cotes.");

        renderer.Line();
        renderer.Bullet(DescribeOverlap(fresh, stale));

        if (stale.Trace.IndexModelWarning is { } warning)
        {
            renderer.Bullet(
                "Le seul signal existant est un champ de la trace, que personne n'est oblige de lire : " +
                warning);
        }
        else
        {
            renderer.Bullet(
                $"Il n'y a meme pas d'avertissement : l'index se declare construit par « {secondModel} » " +
                $"et les questions sont encodees par « {firstModel} » — deux noms que rien ne distingue. " +
                "La detection repose entierement sur la comparaison de ces noms ; un faux qui emprunterait " +
                "le nom d'un vrai modele rendrait la substitution parfaitement indetectable. C'est " +
                "pourquoi la racine de composition refuse de laisser l'embedding factice porter un nom " +
                "configure.");
        }

        await RenderStrictContrast(
            commandLine, sharedIndex, command, firstModel, firstDimension, renderer, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>L'autre monde : la meme requete, avec le controle index/modele arme.</summary>
    private static async Task RenderStrictContrast(
        CommandLine commandLine,
        IVectorIndex sharedIndex,
        AnswerQuestionCommand command,
        string firstModel,
        int firstDimension,
        ConsoleRenderer renderer,
        CancellationToken cancellationToken)
    {
        renderer.Line();
        renderer.Paragraph(
            "Maintenant la MEME requete, sur le MEME index perime, avec " +
            "Pipeline:StrictIndexModelCheck a true (option --strict) :");

        using var hostStrict = CreateHost(commandLine, firstModel, firstDimension, strict: true, sharedIndex);

        try
        {
            var result = await hostStrict.Service<AnswerQuestionUseCase>()
                .ExecuteAsync(command, cancellationToken)
                .ConfigureAwait(false);

            renderer.Bullet(
                "Aucune exception : le nom du modele coincide avec celui qui a construit l'index, il n'y " +
                "a donc rien a detecter. Issue : " + AskCommand.SummariseOutcome(result.Outcome));
        }
        catch (InvalidOperationException exception)
        {
            renderer.Bullet($"InvalidOperationException — {exception.Message}");

            renderer.Line();
            renderer.Paragraph(
                "Meme index, meme question, meme code : un booleen de configuration separe une reponse " +
                "plausible et fausse d'un echec franc et immediat. Le depot expose les deux mondes, et le " +
                "defaut permissif est delibere — c'est lui qui rend le scenario observable plutot que " +
                "theorique. En production, le choix se discute : echouer au demarrage coute une " +
                "interruption, repondre a cote coute la confiance, et personne ne s'en apercoit.");
        }
    }

    /// <summary>
    /// Un montage supplementaire, branche sur l'index deja construit. Il ne partage QUE
    /// l'index : ses embeddings et ses reglages de pipeline lui sont propres, ce qui est
    /// exactement la situation d'un processus redemarre avec une autre configuration.
    /// </summary>
    private static CliHost CreateHost(
        CommandLine commandLine,
        string model,
        int dimension,
        bool strict,
        IVectorIndex sharedIndex)
    {
        var options = CliConfiguration.Load(commandLine);
        options.Embeddings.Model = model;
        options.Embeddings.Dimension = dimension;
        options.Pipeline.StrictIndexModelCheck = strict;

        return CliHost.Create(options, services => services.AddSingleton<IVectorIndex>(sharedIndex));
    }

    /// <summary>Combien de morceaux les deux classements ont-ils en commun ?</summary>
    private static string DescribeOverlap(AnswerQuestionResult fresh, AnswerQuestionResult stale)
    {
        var freshKeys = fresh.Trace.FromIndex.Select(AskCommand.FragmentKey).ToList();
        var staleKeys = stale.Trace.FromIndex.Select(AskCommand.FragmentKey).ToList();
        var common = freshKeys.Intersect(staleKeys, StringComparer.Ordinal).Count();

        if (freshKeys.Count == 0 || staleKeys.Count == 0)
        {
            return "L'un des deux classements est vide : le seuil de score a tout ecarte d'un cote et pas " +
                   "de l'autre, ce qui est deja une divergence complete.";
        }

        return common == 0
            ? $"Les deux classements n'ont AUCUN morceau en commun : {Number(freshKeys.Count)} rendus a " +
              $"jour, {Number(staleKeys.Count)} rendus perimes, zero recouvrement. La reponse produite " +
              "reste bien formee et citee — elle s'appuie sur du materiau sans rapport avec la question."
            : $"{Number(common)} morceau(x) en commun sur {Number(freshKeys.Count)} rendus a jour et " +
              $"{Number(staleKeys.Count)} rendus perimes. Ce qui reste commun n'est pas rassurant : c'est " +
              "ce qui masque la derive au premier coup d'oeil.";
    }

    private static string Block(string title, IReadOnlyList<ScoredFragment> fragments)
    {
        if (fragments.Count == 0)
        {
            return title + " : (aucun)";
        }

        var lines = fragments.Select(scored =>
            $"{ConsoleRenderer.Score(scored.Score)}  {AskCommand.FragmentKey(scored)}");

        return title + " :\n" + string.Join("\n", lines);
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
