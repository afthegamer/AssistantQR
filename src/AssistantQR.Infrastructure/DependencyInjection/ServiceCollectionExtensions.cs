using AssistantQR.Application.Model;
using AssistantQR.Application.Ports;
using AssistantQR.Application.UseCases.AnswerQuestion;
using AssistantQR.Application.UseCases.IndexCorpus;
using AssistantQR.Application.UseCases.Snapshots;

using AssistantQR.Infrastructure.Chunking;
using AssistantQR.Infrastructure.Configuration;
using AssistantQR.Infrastructure.Corpus;
using AssistantQR.Infrastructure.Diagnostics;
using AssistantQR.Infrastructure.Embeddings;
using AssistantQR.Infrastructure.LanguageModels;
using AssistantQR.Infrastructure.Prompts;
using AssistantQR.Infrastructure.Snapshots;
using AssistantQR.Infrastructure.Time;
using AssistantQR.Infrastructure.VectorIndex;

using Microsoft.Extensions.DependencyInjection;

namespace AssistantQR.Infrastructure.DependencyInjection;

/// <summary>
/// Le montage : c'est ici, et uniquement ici, que les ports de l'Application recoivent
/// une implementation.
/// </summary>
/// <remarks>
/// CE FICHIER EST LA RACINE DE COMPOSITION. Aucune autre classe du depot ne fait un
/// <c>new</c> sur un adaptateur : les cas d'usage recoivent des interfaces, le Domain ne
/// recoit rien du tout. Toute la connaissance « quelle piece concrete branche-t-on » est
/// concentree en un point, ce qui donne au cours une propriete rare : la liste complete
/// des dependances externes du systeme tient sur un ecran, et on peut la lire a voix
/// haute.
///
/// SCENARIO A — CHANGER DE MODELE, C'EST CHANGER UNE LIGNE DE CONFIGURATION.
/// La demonstration se joue dans <see cref="AddPorts"/>. Passer
/// <c>AssistantQR:LanguageModel:Model</c> de « extractive-fake » a « granite4.2:3b », ou
/// <c>AssistantQR:Profile</c> de « offline » a « local », remplace un faux deterministe
/// par un modele reel interroge en HTTP. Ce qui NE change pas est la partie
/// interessante : pas une ligne du Domain, pas une ligne des cas d'usage, pas un test de
/// politique metier. La garantie « une reponse cite ses sources ou n'existe pas » tient
/// independamment du modele, parce qu'elle n'a jamais ete confiee au modele.
/// </remarks>
public static class ServiceCollectionExtensions
{
    /// <summary>Nom du client HTTP du service Python (embeddings et index vectoriel).</summary>
    internal const string EmbeddingsClientName = "embeddings";

    /// <summary>Nom du client HTTP d'Ollama.</summary>
    internal const string OllamaClientName = "ollama";

    /// <summary>Enregistre les huit ports, les cas d'usage et les reglages du pipeline.</summary>
    /// <exception cref="InvalidOperationException">
    /// Si le profil est inconnu, si une URL est invalide, ou si un reglage du pipeline est
    /// hors de son domaine de validite. On echoue au montage : un systeme mal configure
    /// qui demarre quand meme produit des refus qu'on attribuera au modele.
    /// </exception>
    public static IServiceCollection AddAssistantQR(this IServiceCollection services, AssistantOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        EnsureKnownProfile(options);
        EnsureNoUnannouncedFakes(options);

        var pipeline = options.ToPipelineOptions();
        pipeline.Validate();

        services.AddSingleton(options);
        services.AddSingleton(pipeline);

        AddNamedHttpClients(services, options);
        AddPorts(services, options);
        AddUseCases(services);

        return services;
    }

    /// <summary>
    /// LA PAGE A PROJETER. Huit ports, huit lignes de branchement, et deux questions
    /// posees a la configuration. Tout ce que le systeme sait du monde exterieur est ici.
    /// </summary>
    /// <remarks>
    /// Les cinq premiers ports ne dependent pas du profil : le corpus, les gabarits et les
    /// instantanes sont des fichiers du depot dans les deux cas, l'horloge est l'horloge,
    /// et la strategie de decoupage se choisit par son nom sans jamais toucher au reseau.
    /// Les trois derniers — embeddings, index, modele de langue — basculent, et c'est la
    /// toute la demonstration du scenario A.
    ///
    /// Le couple embeddings + index bascule d'un seul bloc, parce que les deux vivent dans
    /// le meme service Python. Garder l'index distant tout en calculant les vecteurs avec
    /// le faux enverrait des vecteurs haches a un index construit avec un vrai modele :
    /// l'index repondrait sans broncher, avec des scores parfaitement plausibles.
    /// </remarks>
    private static void AddPorts(IServiceCollection services, AssistantOptions options)
    {
        // Chemins resolus une fois, au montage : les adaptateurs recoivent de l'absolu et
        // leurs messages d'erreur peuvent nommer un chemin que l'on peut aller voir.
        var corpusDirectory = PathResolver.Resolve(options.CorpusDirectory);
        var promptsDirectory = PathResolver.Resolve(options.PromptsDirectory);
        var snapshotsDirectory = PathResolver.Resolve(options.SnapshotsDirectory);

        // --- Les cinq ports que le profil ne touche pas ------------------------------
        services.AddSingleton<IDocumentRepository>(_ => new FileSystemDocumentRepository(corpusDirectory));
        services.AddSingleton<IPromptCatalog>(_ => new FileSystemPromptCatalog(promptsDirectory));
        services.AddSingleton<ISnapshotStore>(_ => new JsonFileSnapshotStore(snapshotsDirectory));
        services.AddSingleton<IClock>(_ => SystemClock.Instance);
        services.AddSingleton<IChunkingStrategy>(_ => ChunkingStrategyFactory.Create(options.ChunkingStrategy));

        // --- Les trois ports qui basculent : le scenario A ---------------------------
        var remote = UsesRemoteEmbeddings(options);

        services.AddSingleton<IEmbeddingService>(provider => remote
            ? new HttpEmbeddingService(
                EmbeddingsClient(provider),
                new EmbeddingModelDescriptor(options.Embeddings.Model.Trim(), options.Embeddings.Dimension))
            : CreateHashingEmbeddings(options));

        services.AddSingleton<IVectorIndex>(provider => remote
            ? new HttpVectorIndex(EmbeddingsClient(provider))
            : new InMemoryVectorIndex(provider.GetRequiredService<IClock>()));

        services.AddSingleton<ILanguageModel>(provider => CreateLanguageModel(provider, options));
    }

    /// <summary>Graine de projection de la seconde variante du faux d'embeddings.</summary>
    /// <remarks>
    /// La valeur (le nombre d'or sur 32 bits) n'a aucune vertu particuliere : n'importe
    /// quel entier different de <see cref="HashingEmbeddingService.DefaultSeed"/> ferait
    /// l'affaire. Ce qui compte est qu'elle soit FIGEE — une graine tiree au hasard au
    /// demarrage rendrait les instantanes incomparables d'une execution a l'autre, et
    /// transformerait le detecteur de derive en detecteur de fumee qu'on debranche.
    /// </remarks>
    internal const uint HashingSeedB = 0x9E3779B1;

    /// <summary>
    /// LA SECONDE LIGNE QUE LE COURS PROJETTE. Deux noms reserves, deux graines, une
    /// seule dimension : « hashing-fake » et « hashing-fake-b » sont deux modeles
    /// d'embeddings differents au sens ou le systeme peut le constater — noms distincts
    /// dans les metadonnees de l'index, vecteurs distincts pour le meme texte — et
    /// indiscernables au sens ou tout continue de fonctionner. C'est exactement la
    /// situation de deux vrais modeles concurrents en dimension 1024.
    /// </summary>
    /// <remarks>
    /// Le faux garde toujours l'un de ses deux noms reserves, jamais un nom arbitraire
    /// venu de la configuration : ce nom part dans les metadonnees de l'index, et un faux
    /// qui se ferait passer pour « qwen3-embedding:0.6b » rendrait indetectable
    /// l'incoherence modele/index que le scenario B cherche justement a montrer.
    /// </remarks>
    private static HashingEmbeddingService CreateHashingEmbeddings(AssistantOptions options)
    {
        var dimension = options.Embeddings.Dimension;

        return IsHashingVariantB(options.Embeddings.Model)
            ? new HashingEmbeddingService(dimension, EmbeddingOptions.HashingModelB, HashingSeedB)
            : new HashingEmbeddingService(dimension, EmbeddingOptions.HashingModel);
    }

    /// <summary>
    /// Le nom du modele designe soit l'un des deux faux, soit un modele Ollama reel.
    /// L'ambiguite est voulue : le reglage que l'on modifie pour changer de modele reste
    /// le meme, qu'on aille vers un faux ou vers un vrai.
    /// </summary>
    private static ILanguageModel CreateLanguageModel(IServiceProvider provider, AssistantOptions options)
    {
        var model = options.LanguageModel.Model?.Trim() ?? string.Empty;

        if (string.Equals(model, LanguageModelOptions.ReplayModel, StringComparison.OrdinalIgnoreCase))
        {
            // Le repli sur l'extracteur est ce qui rend le rejeu utilisable : une question
            // absente du fichier recoit quand meme une reponse, et la demonstration ne
            // s'arrete pas sur un cas non prevu.
            return ReplayLanguageModel.FromJsonFile(
                PathResolver.Resolve(options.LanguageModel.ReplayFile),
                new ExtractiveLanguageModel());
        }

        return UsesOllama(options)
            ? new OllamaLanguageModel(OllamaClient(provider), model)
            : new ExtractiveLanguageModel();
    }

    /// <summary>
    /// Les cas d'usage sont des classes concretes, pas des interfaces : ils n'ont pas de
    /// double et l'on ne les remplace jamais. Les enregistrer sert seulement a laisser le
    /// conteneur leur passer les ports dont ils dependent.
    /// </summary>
    private static void AddUseCases(IServiceCollection services)
    {
        services.AddSingleton<AnswerQuestionUseCase>();
        services.AddSingleton<IndexCorpusUseCase>();
        services.AddSingleton<RecordSnapshotUseCase>();
        services.AddSingleton<CompareSnapshotsUseCase>();

        services.AddSingleton<EnvironmentProbe>();
    }

    /// <summary>
    /// Les deux clients sont enregistres dans les deux profils. En « offline » aucun
    /// adaptateur ne les demandera, mais la sonde de diagnostic a besoin d'une fabrique de
    /// clients pour dire si les services sont la — y compris quand on ne s'en sert pas.
    /// </summary>
    private static void AddNamedHttpClients(IServiceCollection services, AssistantOptions options)
    {
        var embeddingsUri = ParseBaseAddress(options.Embeddings.ServiceUrl, "AssistantQR:Embeddings:ServiceUrl");
        var ollamaUri = ParseBaseAddress(options.LanguageModel.OllamaUrl, "AssistantQR:LanguageModel:OllamaUrl");

        services.AddHttpClient(EmbeddingsClientName, client =>
        {
            client.BaseAddress = embeddingsUri;
            client.Timeout = TimeSpan.FromSeconds(Math.Max(1, options.Embeddings.TimeoutSeconds));
        });

        services.AddHttpClient(OllamaClientName, client =>
        {
            client.BaseAddress = ollamaUri;

            // Le delai est genereux : sur processeur, une generation de six cents jetons
            // se compte en dizaines de secondes. Un delai serre transformerait une lenteur
            // attendue en panne inexplicable.
            client.Timeout = TimeSpan.FromSeconds(Math.Max(1, options.LanguageModel.TimeoutSeconds));
        });
    }

    /// <summary>
    /// Le service Python est-il sollicite par cette configuration ? Non en profil
    /// « offline », et non plus si l'on a demande explicitement l'embedding factice — ce
    /// qui permet d'essayer un vrai modele de langue sans demarrer le service Python.
    /// </summary>
    internal static bool UsesRemoteEmbeddings(AssistantOptions options) =>
        !options.IsOffline && !IsHashingModel(options.Embeddings.Model);

    /// <summary>Le nom designe-t-il l'une des deux variantes du faux d'embeddings ?</summary>
    internal static bool IsHashingModel(string? model)
    {
        var trimmed = model?.Trim();

        return string.Equals(trimmed, EmbeddingOptions.HashingModel, StringComparison.OrdinalIgnoreCase)
            || IsHashingVariantB(model);
    }

    private static bool IsHashingVariantB(string? model) =>
        string.Equals(model?.Trim(), EmbeddingOptions.HashingModelB, StringComparison.OrdinalIgnoreCase);

    /// <summary>Ollama est-il sollicite ? Non hors ligne, et non si le nom designe un faux.</summary>
    internal static bool UsesOllama(AssistantOptions options) =>
        !options.IsOffline && !IsReservedFakeName(options.LanguageModel.Model);

    private static bool IsReservedFakeName(string? model)
    {
        var trimmed = model?.Trim();

        return string.Equals(trimmed, LanguageModelOptions.ExtractiveModel, StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, LanguageModelOptions.ReplayModel, StringComparison.OrdinalIgnoreCase);
    }

    private static HttpClient EmbeddingsClient(IServiceProvider provider) =>
        provider.GetRequiredService<IHttpClientFactory>().CreateClient(EmbeddingsClientName);

    private static HttpClient OllamaClient(IServiceProvider provider) =>
        provider.GetRequiredService<IHttpClientFactory>().CreateClient(OllamaClientName);

    /// <summary>
    /// Un profil inconnu n'est pas rabattu en douce sur « offline » : les deux profils ne
    /// donnent pas les memes reponses, et une faute de frappe qui basculerait le systeme
    /// sur des faux sans le dire serait la pire des pannes silencieuses.
    /// </summary>
    private static void EnsureKnownProfile(AssistantOptions options)
    {
        var profile = options.Profile?.Trim() ?? string.Empty;

        if (string.Equals(profile, AssistantOptions.OfflineProfile, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(profile, AssistantOptions.LocalProfile, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Profil inconnu : « {options.Profile} ». Valeurs acceptées : " +
            $"« {AssistantOptions.OfflineProfile} » (tout factice, aucun réseau) ou " +
            $"« {AssistantOptions.LocalProfile} » (service Python et Ollama en local).");
    }

    /// <summary>
    /// GARDE-FOU CONTRE LA PANNE MUETTE. En profil « local », un nom de doublure laisse
    /// dans la configuration donnait la doublure — sans un mot, sans un avertissement,
    /// avec des scores parfaitement plausibles. La composition refuse desormais de
    /// demarrer et nomme la cle a corriger.
    /// </summary>
    /// <remarks>
    /// Le controle est ici, au montage, et pas a l'usage : une panne de configuration
    /// doit tomber avant la premiere question, pas au milieu d'une demonstration. Il ne
    /// remplace pas les erreurs d'execution des adaptateurs reels — service Python
    /// injoignable, modele Ollama absent — qui remontent telles quelles et arretent le
    /// programme. Aucun chemin de code ne rattrape un adaptateur reel pour lui substituer
    /// une doublure : c'est verifiable en cherchant « catch » dans ce projet.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Si le profil est « local » et qu'une doublure est branchee sans avoir ete demandee.
    /// </exception>
    private static void EnsureNoUnannouncedFakes(AssistantOptions options)
    {
        if (options.IsOffline || options.AllowFakeAdapters)
        {
            return;
        }

        var fakes = new List<string>();

        if (IsHashingModel(options.Embeddings.Model))
        {
            fakes.Add(
                $"  AssistantQR:Embeddings:Model = « {options.Embeddings.Model?.Trim()} » " +
                "— doublure d'embeddings (hachage lexical, aucune sémantique)");
        }

        if (IsReservedFakeName(options.LanguageModel.Model))
        {
            fakes.Add(
                $"  AssistantQR:LanguageModel:Model = « {options.LanguageModel.Model?.Trim()} » " +
                "— doublure de modèle de langue (extraction, aucune génération)");
        }

        if (fakes.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Profil « {AssistantOptions.LocalProfile} » demandé, mais une doublure est encore branchée :" +
            Environment.NewLine +
            string.Join(Environment.NewLine, fakes) +
            Environment.NewLine + Environment.NewLine +
            "Le système aurait répondu sans erreur, avec des scores plausibles, et vous auriez cru " +
            "mesurer un vrai modèle. C'est refusé plutôt que toléré en silence." +
            Environment.NewLine + Environment.NewLine +
            "Deux issues :" + Environment.NewLine +
            "  - mettre un vrai nom de modèle (par exemple « qwen3-embedding:0.6b », « bge-m3 », " +
            "« granite4.2:3b ») ;" + Environment.NewLine +
            "  - ou assumer la doublure en posant AssistantQR:AllowFakeAdapters = true.");
    }

    private static Uri ParseBaseAddress(string? url, string settingName)
    {
        if (Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return uri;
        }

        throw new InvalidOperationException(
            $"L'adresse « {url} » du paramètre {settingName} n'est pas une URL http(s) valide. " +
            "Exemple attendu : http://localhost:8088");
    }
}
