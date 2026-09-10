using AssistantQR.Application.Configuration;
using AssistantQR.Application.Model;
using AssistantQR.Application.Ports;
using AssistantQR.Application.UseCases.AnswerQuestion;
using AssistantQR.Application.UseCases.IndexCorpus;
using AssistantQR.Application.UseCases.Snapshots;
using AssistantQR.Domain.Access;
using AssistantQR.Domain.Documents;
using AssistantQR.Infrastructure.Chunking;
using AssistantQR.Infrastructure.Configuration;
using AssistantQR.Infrastructure.Corpus;
using AssistantQR.Infrastructure.DependencyInjection;
using AssistantQR.Infrastructure.Diagnostics;
using AssistantQR.Infrastructure.Embeddings;
using AssistantQR.Infrastructure.LanguageModels;
using AssistantQR.Infrastructure.Prompts;
using AssistantQR.Infrastructure.Snapshots;
using AssistantQR.Infrastructure.Tests.Support;
using AssistantQR.Infrastructure.Time;
using AssistantQR.Infrastructure.VectorIndex;

using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace AssistantQR.Infrastructure.Tests.DependencyInjection;

/// <summary>
/// Tests de la racine de composition.
/// </summary>
/// <remarks>
/// CE FICHIER EST LA PREUVE DU SCENARIO A : « changer de modele, c'est changer une ligne
/// de configuration ». Ce n'est pas une formule de brochure, c'est une propriete
/// verifiable — et la voici verifiee. Le meme appel a <c>AddAssistantQR</c>, avec une
/// seule chaine differente, rend soit un extracteur de trente lignes soit un client
/// HTTP vers Ollama, soit un sac de mots hache soit un service Python.
///
/// CE QUE CES TESTS NE MONTRENT PAS EST AUSSI IMPORTANT : nulle part ils ne touchent au
/// Domain, aux cas d'usage ni a la politique metier. C'est bien le point. La garantie
/// « une reponse cite ses sources ou n'existe pas » tient independamment du modele,
/// parce qu'elle n'a jamais ete confiee au modele.
///
/// Aucun de ces tests n'ouvre de connexion : les adaptateurs HTTP ne parlent au reseau
/// qu'a l'appel d'une methode, jamais a la construction. On verifie donc le CABLAGE,
/// pas la disponibilite des services — c'est le role des tests d'integration.
/// </remarks>
public sealed class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddAssistantQR_ProfilHorsLigne_ResoutLesHuitPortsSurLeursDoublures()
    {
        using var provider = BuildProvider(OfflineOptions());

        Assert.IsType<FileSystemDocumentRepository>(provider.GetRequiredService<IDocumentRepository>());
        Assert.IsType<ParagraphChunkingStrategy>(provider.GetRequiredService<IChunkingStrategy>());
        Assert.IsType<HashingEmbeddingService>(provider.GetRequiredService<IEmbeddingService>());
        Assert.IsType<InMemoryVectorIndex>(provider.GetRequiredService<IVectorIndex>());
        Assert.IsType<ExtractiveLanguageModel>(provider.GetRequiredService<ILanguageModel>());
        Assert.IsType<FileSystemPromptCatalog>(provider.GetRequiredService<IPromptCatalog>());
        Assert.IsType<SystemClock>(provider.GetRequiredService<IClock>());
        Assert.IsType<JsonFileSnapshotStore>(provider.GetRequiredService<ISnapshotStore>());
    }

    [Fact]
    public void AddAssistantQR_ProfilHorsLigne_ResoutLesCasDUsage()
    {
        using var provider = BuildProvider(OfflineOptions());

        Assert.NotNull(provider.GetRequiredService<AnswerQuestionUseCase>());
        Assert.NotNull(provider.GetRequiredService<IndexCorpusUseCase>());
        Assert.NotNull(provider.GetRequiredService<RecordSnapshotUseCase>());
        Assert.NotNull(provider.GetRequiredService<CompareSnapshotsUseCase>());
        Assert.NotNull(provider.GetRequiredService<EnvironmentProbe>());
    }

    [Fact]
    public void AddAssistantQR_ProfilHorsLigne_IgnoreLesAdressesDesServicesDistants()
    {
        var options = OfflineOptions();
        options.Embeddings.ServiceUrl = "http://une-machine-qui-n-existe-pas.invalid:8088";
        options.LanguageModel.OllamaUrl = "http://une-machine-qui-n-existe-pas.invalid:11434";

        using var provider = BuildProvider(options);

        // Les adresses sont syntaxiquement valides mais ne menent nulle part : le profil
        // hors-ligne resout quand meme les trois ports basculants, preuve qu'aucun
        // adaptateur reseau n'a ete instancie ni sollicite.
        Assert.True(options.IsOffline);
        Assert.IsType<HashingEmbeddingService>(provider.GetRequiredService<IEmbeddingService>());
        Assert.IsType<InMemoryVectorIndex>(provider.GetRequiredService<IVectorIndex>());
        Assert.IsType<ExtractiveLanguageModel>(provider.GetRequiredService<ILanguageModel>());
    }

    [Fact]
    public void AddAssistantQR_ProfilLocal_ResoutLesTroisPortsBasculantsSurLeursAdaptateursHttp()
    {
        var options = OfflineOptions();
        options.Profile = AssistantOptions.LocalProfile;
        options.Embeddings.Model = "qwen3-embedding:0.6b";
        options.Embeddings.Dimension = 1024;
        options.LanguageModel.Model = "granite4.2:3b";

        using var provider = BuildProvider(options);

        // Une seule chaine a change dans la configuration ; trois adaptateurs ont bascule.
        Assert.IsType<HttpEmbeddingService>(provider.GetRequiredService<IEmbeddingService>());
        Assert.IsType<HttpVectorIndex>(provider.GetRequiredService<IVectorIndex>());
        Assert.IsType<OllamaLanguageModel>(provider.GetRequiredService<ILanguageModel>());
    }

    [Fact]
    public void AddAssistantQR_ProfilLocal_NeChangeRienAuxCinqPortsQuiNeDependentPasDuReseau()
    {
        var options = OfflineOptions();
        options.Profile = AssistantOptions.LocalProfile;
        options.Embeddings.Model = "qwen3-embedding:0.6b";
        options.Embeddings.Dimension = 1024;
        options.LanguageModel.Model = "granite4.2:3b";

        using var provider = BuildProvider(options);

        // Le corpus, les gabarits et les instantanes sont des fichiers du depot dans les
        // deux profils ; l'horloge est l'horloge ; la strategie de decoupage se choisit
        // par son nom sans jamais toucher au reseau.
        Assert.IsType<FileSystemDocumentRepository>(provider.GetRequiredService<IDocumentRepository>());
        Assert.IsType<FileSystemPromptCatalog>(provider.GetRequiredService<IPromptCatalog>());
        Assert.IsType<JsonFileSnapshotStore>(provider.GetRequiredService<ISnapshotStore>());
        Assert.IsType<SystemClock>(provider.GetRequiredService<IClock>());
        Assert.IsType<ParagraphChunkingStrategy>(provider.GetRequiredService<IChunkingStrategy>());
    }

    /// <summary>
    /// Ce test testait la panne muette. Il affirmait qu'un faux branche en profil
    /// « local » etait garde — ce qui etait vrai, et silencieux : on croyait mesurer un
    /// vrai modele d'embeddings, on mesurait trente lignes de hachage, et rien ne le
    /// disait. Le comportement attendu est desormais l'inverse : la composition refuse.
    /// </summary>
    [Fact]
    public void AddAssistantQR_ProfilLocalAvecEmbeddingFactice_Refuse()
    {
        var options = OfflineOptions();
        options.Profile = AssistantOptions.LocalProfile;
        options.Embeddings.Model = EmbeddingOptions.HashingModel;
        options.LanguageModel.Model = "granite4.2:3b";

        var exception = Assert.Throws<InvalidOperationException>(() => BuildProvider(options).Dispose());

        Assert.Contains(EmbeddingOptions.HashingModel, exception.Message, StringComparison.Ordinal);
        Assert.Contains("AllowFakeAdapters", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// La combinaison reste possible — essayer un vrai modele de langue sans demarrer le
    /// service Python est legitime — mais elle doit etre DEMANDEE. Une doublure assumee
    /// n'est plus une panne muette : c'est une decision, et elle est ecrite quelque part.
    /// </summary>
    [Fact]
    public void AddAssistantQR_ProfilLocalAvecEmbeddingFacticeAssume_GardeLeFauxEtLeVraiModeleDeLangue()
    {
        var options = OfflineOptions();
        options.Profile = AssistantOptions.LocalProfile;
        options.Embeddings.Model = EmbeddingOptions.HashingModel;
        options.LanguageModel.Model = "granite4.2:3b";
        options.AllowFakeAdapters = true;

        using var provider = BuildProvider(options);

        Assert.IsType<HashingEmbeddingService>(provider.GetRequiredService<IEmbeddingService>());
        Assert.IsType<InMemoryVectorIndex>(provider.GetRequiredService<IVectorIndex>());
        Assert.IsType<OllamaLanguageModel>(provider.GetRequiredService<ILanguageModel>());
    }

    /// <summary>
    /// Le symetrique : un vrai service d'embeddings mais une doublure de modele de langue
    /// oubliee dans la configuration. Meme refus, meme raison.
    /// </summary>
    [Fact]
    public void AddAssistantQR_ProfilLocalAvecModeleDeLangueFactice_Refuse()
    {
        var options = OfflineOptions();
        options.Profile = AssistantOptions.LocalProfile;
        options.Embeddings.Model = "qwen3-embedding:0.6b";
        options.LanguageModel.Model = LanguageModelOptions.ExtractiveModel;

        var exception = Assert.Throws<InvalidOperationException>(() => BuildProvider(options).Dispose());

        Assert.Contains(LanguageModelOptions.ExtractiveModel, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddAssistantQR_SecondeVarianteDuFaux_EstResolueEtProduitDAutresVecteurs()
    {
        var optionsA = OfflineOptions();
        optionsA.Embeddings.Model = EmbeddingOptions.HashingModel;
        optionsA.Embeddings.Dimension = 1024;

        var optionsB = OfflineOptions();
        optionsB.Embeddings.Model = EmbeddingOptions.HashingModelB;
        optionsB.Embeddings.Dimension = 1024;

        using var providerA = BuildProvider(optionsA);
        using var providerB = BuildProvider(optionsB);

        var embeddingsA = Assert.IsType<HashingEmbeddingService>(providerA.GetRequiredService<IEmbeddingService>());
        var embeddingsB = Assert.IsType<HashingEmbeddingService>(providerB.GetRequiredService<IEmbeddingService>());

        // UNE LIGNE DE CONFIGURATION, DEUX MODELES D'EMBEDDINGS. Meme dimension, meme
        // algorithme, autre graine de projection : c'est la substitution que le scenario B
        // met en scene, et elle tient tout entiere dans le nom du modele.
        Assert.Equal(EmbeddingOptions.HashingModel, embeddingsA.Model.Name);
        Assert.Equal(EmbeddingOptions.HashingModelB, embeddingsB.Model.Name);
        Assert.Equal(embeddingsA.Model.Dimension, embeddingsB.Model.Dimension);
        Assert.NotEqual(embeddingsA.Seed, embeddingsB.Seed);

        const string question = "Quels sont les horaires d'ouverture le samedi ?";
        Assert.NotEqual(await embeddingsA.EmbedQueryAsync(question), await embeddingsB.EmbedQueryAsync(question));
    }

    /// <summary>
    /// La seconde variante du faux n'echappe pas au garde-fou : elle porte un nom de
    /// doublure, elle est donc refusee en profil « local » comme la premiere. Assumee,
    /// elle est gardee, et elle garde son nom reserve dans les metadonnees de l'index.
    /// </summary>
    [Fact]
    public void AddAssistantQR_ProfilLocalAvecSecondeVarianteDuFaux_RefuseSaufSiAssumee()
    {
        var options = OfflineOptions();
        options.Profile = AssistantOptions.LocalProfile;
        options.Embeddings.Model = EmbeddingOptions.HashingModelB;
        options.LanguageModel.Model = "granite4.2:3b";

        Assert.Throws<InvalidOperationException>(() => BuildProvider(options).Dispose());

        options.AllowFakeAdapters = true;

        using var provider = BuildProvider(options);

        var embeddings = Assert.IsType<HashingEmbeddingService>(provider.GetRequiredService<IEmbeddingService>());

        Assert.Equal(EmbeddingOptions.HashingModelB, embeddings.Model.Name);
        Assert.IsType<InMemoryVectorIndex>(provider.GetRequiredService<IVectorIndex>());
    }

    [Fact]
    public async Task AddAssistantQR_IndexInterrogeAvecLAutreVariante_NeLeveAucuneException()
    {
        var optionsA = OfflineOptions();
        optionsA.Embeddings.Model = EmbeddingOptions.HashingModel;
        optionsA.Embeddings.Dimension = 1024;

        var optionsB = OfflineOptions();
        optionsB.Embeddings.Model = EmbeddingOptions.HashingModelB;
        optionsB.Embeddings.Dimension = 1024;

        using var providerA = BuildProvider(optionsA);
        using var providerB = BuildProvider(optionsB);

        var embeddingsA = providerA.GetRequiredService<IEmbeddingService>();
        var embeddingsB = providerB.GetRequiredService<IEmbeddingService>();
        var index = providerA.GetRequiredService<IVectorIndex>();

        await index.ResetAsync(embeddingsA.Model, "whole-document");

        var chunk = new Chunk(
            "horaires-ouverture#0",
            DocumentId.From("horaires-ouverture"),
            "Horaires d'ouverture au public",
            AccessLevel.Public,
            0,
            "La médiathèque ouvre le samedi de 10 h à 18 h.");

        var documentVectors = await embeddingsA.EmbedDocumentsAsync(new[] { chunk.Text });
        await index.UpsertAsync(new[] { new IndexedChunk(chunk, documentVectors[0]) });

        // LA PANNE SILENCIEUSE, EN TEST. L'index a ete construit par « hashing-fake » et on
        // l'interroge avec un vecteur de « hashing-fake-b ». Meme dimension, donc rien ne
        // s'y oppose : aucune exception, un classement complet, des scores plausibles — et
        // des coordonnees qui ne veulent plus rien dire. Ce test passe au VERT, et c'est
        // exactement ce qu'il doit prouver : la suite de tests ne protege de rien ici.
        var foreignQuery = await embeddingsB.EmbedQueryAsync("horaires du samedi");
        var results = await index.SearchAsync(foreignQuery, 4, SearchFilter.NoFilter);

        Assert.NotNull(results);

        var metadata = await index.GetMetadataAsync();
        Assert.Equal(EmbeddingOptions.HashingModel, metadata.EmbeddingModel);
        Assert.NotEqual(metadata.EmbeddingModel, embeddingsB.Model.Name);
    }

    [Fact]
    public void AddAssistantQR_ModeleDeRejeu_ResoutLeFauxDeRejeu()
    {
        using var directory = new TempDirectory("montage-rejeu");
        var replayFile = directory.Write("replay-llm.json",
            """[ { "match": "horaires", "response": "Ouvert de 10 h à 19 h. [horaires-ouverture]" } ]""");

        var options = OfflineOptions();
        options.LanguageModel.Model = LanguageModelOptions.ReplayModel;
        options.LanguageModel.ReplayFile = replayFile;

        using var provider = BuildProvider(options);

        Assert.IsType<ReplayLanguageModel>(provider.GetRequiredService<ILanguageModel>());
    }

    [Theory]
    [InlineData("paragraph", typeof(ParagraphChunkingStrategy))]
    [InlineData("whole-document", typeof(WholeDocumentChunkingStrategy))]
    [InlineData("fixed-300-50", typeof(FixedSizeChunkingStrategy))]
    public void AddAssistantQR_StrategieDeDecoupage_SuitLaConfiguration(string id, Type expected)
    {
        var options = OfflineOptions();
        options.ChunkingStrategy = id;

        using var provider = BuildProvider(options);

        // Le reglage qui change le plus de choses sans casser la moindre compilation.
        Assert.IsType(expected, provider.GetRequiredService<IChunkingStrategy>());
    }

    [Fact]
    public void AddAssistantQR_ReglagesDuPipeline_SontTraduitsPourLApplication()
    {
        var options = OfflineOptions();
        options.Pipeline.TopK = 7;
        options.Pipeline.MinScore = 0.35;
        options.Pipeline.AccessFilterMode = "pre";
        options.Pipeline.ExcerptLength = 180;

        using var provider = BuildProvider(options);

        var pipeline = provider.GetRequiredService<PipelineOptions>();

        // L'Application ne doit connaitre ni le nom d'une section de configuration, ni
        // l'orthographe qu'on a choisie pour un mode de filtrage : la traduction se fait
        // au montage, et nulle part ailleurs.
        Assert.Equal(7, pipeline.TopK);
        Assert.Equal(0.35, pipeline.MinScore);
        Assert.Equal(AccessFilterMode.Pre, pipeline.FilterMode);
        Assert.Equal(180, pipeline.ExcerptLength);
    }

    [Fact]
    public void AddAssistantQR_ProfilInconnu_EchoueAuMontageEnListantLesValeursAcceptees()
    {
        var options = OfflineOptions();
        options.Profile = "offlin";

        // Un profil inconnu n'est pas rabattu en douce sur « offline » : une faute de
        // frappe qui basculerait le systeme sur des faux sans le dire serait la pire des
        // pannes silencieuses.
        var exception = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddAssistantQR(options));

        Assert.Contains("offlin", exception.Message, StringComparison.Ordinal);
        Assert.Contains("offline", exception.Message, StringComparison.Ordinal);
        Assert.Contains("local", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddAssistantQR_ModeDeFiltrageInconnu_EchoueAuMontage()
    {
        var options = OfflineOptions();
        options.Pipeline.AccessFilterMode = "apres";

        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddAssistantQR(options));
    }

    [Fact]
    public void AddAssistantQR_ReglageDePipelineHorsBornes_EchoueAuMontage()
    {
        var options = OfflineOptions();
        options.Pipeline.TopK = 0;

        // On echoue au montage : un systeme mal configure qui demarre quand meme produit
        // des refus qu'on attribuera au modele.
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddAssistantQR(options));
    }

    [Fact]
    public void AddAssistantQR_UrlInvalide_EchoueAuMontageEnNommantLeParametre()
    {
        var options = OfflineOptions();
        options.Embeddings.ServiceUrl = "pas-une-url";

        var exception = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddAssistantQR(options));

        Assert.Contains("AssistantQR:Embeddings:ServiceUrl", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddAssistantQR_PortsResolusDeuxFois_RendentLaMemeInstance()
    {
        using var provider = BuildProvider(OfflineOptions());

        // Les ports sont des singletons : l'index en memoire perdrait tout son contenu
        // entre l'indexation et la premiere question s'il en allait autrement.
        Assert.Same(provider.GetRequiredService<IVectorIndex>(), provider.GetRequiredService<IVectorIndex>());
        Assert.Same(provider.GetRequiredService<IDocumentRepository>(), provider.GetRequiredService<IDocumentRepository>());
    }

    private static ServiceProvider BuildProvider(AssistantOptions options) =>
        new ServiceCollection().AddAssistantQR(options).BuildServiceProvider();

    /// <summary>
    /// Configuration hors-ligne par defaut. Les chemins restent relatifs : ils sont
    /// resolus par <c>PathResolver</c> vers le corpus et les gabarits du depot, ce qui
    /// fait de ces tests une verification incidente du montage complet.
    /// </summary>
    private static AssistantOptions OfflineOptions() => new()
    {
        Profile = AssistantOptions.OfflineProfile,
    };
}
