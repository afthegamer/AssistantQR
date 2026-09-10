// ---------------------------------------------------------------------------
// L'ORCHESTRATEUR.
//
// Ce fichier ne contient AUCUNE regle metier. Il ne decide pas si une reponse est
// acceptable — c'est AnswerPolicy qui le fait, dans le Domain, sans dependance.
// Il ne sait pas non plus comment on calcule un vecteur, ou l'on range un index,
// ni comment on parle a un modele de langue : tout cela est derriere des ports.
// Son unique responsabilite est l'ENCHAINEMENT : dans quel ordre, avec quoi, et
// surtout — a quelles conditions on s'arrete avant d'appeler le modele.
//
// Le point le plus important du fichier est l'etape 5. Quand rien n'est lisible,
// on N'APPELLE PAS le modele. Pas par economie : parce qu'un modele a qui l'on ne
// fournit aucun extrait repond quand même, avec aplomb, en puisant dans ses poids.
// Le refus n'est pas ici une degradation du service, c'est le service.
// ---------------------------------------------------------------------------

using System.Diagnostics;

using AssistantQR.Application.Configuration;
using AssistantQR.Application.Model;
using AssistantQR.Application.Ports;
using AssistantQR.Application.Retrieval;
using AssistantQR.Application.UseCases.Snapshots;

using AssistantQR.Domain.Answers;
using AssistantQR.Domain.Evidence;
using AssistantQR.Domain.Policies;

namespace AssistantQR.Application.UseCases.AnswerQuestion;

/// <summary>
/// Repondre a une question en citant ses sources, ou refuser en disant pourquoi.
/// </summary>
public sealed class AnswerQuestionUseCase
{
    private readonly IEmbeddingService _embeddings;
    private readonly IVectorIndex _index;
    private readonly ILanguageModel _languageModel;
    private readonly IPromptCatalog _prompts;
    private readonly PipelineOptions _options;

    /// <summary>
    /// Les quatre ports et les reglages. Aucune implementation concrete n'apparait ici :
    /// c'est ce qui permet de faire tourner ce cas d'usage en test avec un modele
    /// deterministe, hors ligne, et d'obtenir des assertions exactes sur ses refus.
    /// </summary>
    public AnswerQuestionUseCase(
        IEmbeddingService embeddings,
        IVectorIndex index,
        ILanguageModel languageModel,
        IPromptCatalog prompts,
        PipelineOptions options)
    {
        _embeddings = embeddings ?? throw new ArgumentNullException(nameof(embeddings));
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _languageModel = languageModel ?? throw new ArgumentNullException(nameof(languageModel));
        _prompts = prompts ?? throw new ArgumentNullException(nameof(prompts));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>Execute le pipeline complet pour une question.</summary>
    public async Task<AnswerQuestionResult> ExecuteAsync(
        AnswerQuestionCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Chronometre : mesurer le temps n'est pas une entree-sortie, aucune horloge
        // n'est necessaire et le resultat reste reproductible en structure.
        var stopwatch = Stopwatch.StartNew();

        // ETAPE 1 — Un reglage incoherent doit echouer maintenant, pas se traduire
        // plus loin en un refus trompeur.
        _options.Validate();

        // ETAPE 2 — L'index a-t-il été construit avec le modele que l'on s'apprete a
        // utiliser ? Les dimensions peuvent concorder alors que les espaces vectoriels
        // n'ont rien a voir : la recherche repond, les scores semblent normaux, et les
        // resultats sont du bruit. C'est la panne silencieuse du scenario B, et la
        // seule facon de la voir est de comparer explicitement les deux noms.
        var metadata = await _index.GetMetadataAsync(cancellationToken).ConfigureAwait(false)
                       ?? IndexMetadata.Empty;

        var indexModelWarning = BuildIndexModelWarning(metadata);
        if (indexModelWarning is not null && _options.StrictIndexModelCheck)
        {
            // Le mode strict transforme l'avertissement en panne franche. Il est
            // desactive par defaut precisement pour que la demonstration puisse
            // montrer ce qui se passe quand personne ne verifie.
            throw new InvalidOperationException(indexModelWarning);
        }

        // Le gabarit est charge tot pour que son empreinte figure dans la trace même
        // lorsqu'on n'appellera pas le modele. Un catalogue incomplet ne doit pas pour
        // autant masquer un refus legitime : l'echec est differe a l'etape 6, la ou il
        // devient reellement bloquant.
        var template = TryLoadTemplate();
        var configuration = BuildConfiguration(metadata, template);

        // ETAPE 3 — La question devient un vecteur. C'est le seul endroit du pipeline
        // ou le texte de l'usager traverse une frontiere technique.
        var queryVector = await _embeddings
            .EmbedQueryAsync(command.Question.Text, cancellationToken)
            .ConfigureAwait(false);

        // ETAPE 4 — Recherche, avec pre ou post-filtrage selon la configuration.
        var strategy = RetrievalStrategyFactory.Create(_options.FilterMode, _index);
        var retrieval = await strategy
            .RetrieveAsync(queryVector, command.Requester, _options, cancellationToken)
            .ConfigureAwait(false);

        var dropped = ComputeDropped(retrieval.FromIndex, retrieval.AfterAccessFilter);

        // ETAPE 5 — Rien de lisible : ON N'APPELLE PAS LE MODELE.
        // On soumet quand même la decision a la politique du Domain, avec une proposition
        // vide. Ce qu'on lui passe comme fragments decide de l'explication rendue a
        // l'usager, et le choix est plus subtil qu'il n'y parait.
        //
        // ON LUI PASSE LES CANDIDATS QUI ONT FRANCHI LE SEUIL DE SCORE, pas la sortie
        // brute de l'index. C'est exactement l'ensemble sur lequel le controle d'acces a
        // travaille, donc le seul qui reponde a la question « restait-il quelque chose de
        // lisible ? ». Passer la liste brute paraissait plus simple et donnait un refus
        // FAUX : il suffisait qu'un fragment public sans rapport traine sous le seuil pour
        // que la politique conclue « il y avait du lisible » et impute le refus au modele
        // — un modele qu'on vient precisement de ne pas appeler. L'usager lisait alors
        // « le modele n'a rien produit » sous une trace affichant « 0 extrait soumis ».
        //
        // Avec le bon ensemble, la distinction promise fonctionne : aucun candidat au
        // dessus du seuil → « rien dans le corpus » ; des candidats, mais aucun lisible
        // → « la reponse existe et elle n'est pas pour vous ». Deux refus, deux causes,
        // deux phrases differentes.
        if (retrieval.AfterAccessFilter.Count == 0)
        {
            var candidates = RetrievalOrdering.AboveThreshold(retrieval.FromIndex, _options.MinScore);
            var candidateFragments = ToFragments(candidates);
            var refused = AnswerPolicy.Decide(
                command.Question, command.Requester, candidateFragments, DraftAnswer.Empty, _options.ExcerptLength);

            stopwatch.Stop();
            return new AnswerQuestionResult(
                refused,
                BuildTrace(retrieval, dropped, configuration, stopwatch.Elapsed, indexModelWarning));
        }

        // ETAPE 6 — On a de quoi travailler : on construit le prompt, on interroge le
        // modele, on analyse sa sortie, et on la soumet au juge.
        var prompt = RenderPrompt(template, command, retrieval.AfterAccessFilter);

        var completion = await _languageModel
            .CompleteAsync(
                new LlmRequest(prompt, _options.Temperature, _options.Seed, _options.MaxTokens),
                cancellationToken)
            .ConfigureAwait(false);

        var draft = ModelResponseParser.Parse(completion?.Text ?? string.Empty);

        // Les fragments soumis au juge sont EXACTEMENT ceux qui ont été montres au
        // modele. Lui en donner d'autres reviendrait a valider des citations qu'il
        // n'aurait pas pu voir — ou a rejeter des citations legitimes.
        var supplied = ToFragments(retrieval.AfterAccessFilter);
        var outcome = AnswerPolicy.Decide(
            command.Question, command.Requester, supplied, draft, _options.ExcerptLength);

        stopwatch.Stop();
        return new AnswerQuestionResult(
            outcome,
            BuildTrace(retrieval, dropped, configuration, stopwatch.Elapsed, indexModelWarning));
    }

    private string RenderPrompt(
        PromptTemplate? template,
        AnswerQuestionCommand command,
        IReadOnlyList<ScoredFragment> supplied)
    {
        // Si le chargement anticipe avait echoue, on relit maintenant — et cette fois
        // l'exception remonte : sans gabarit, il n'y a pas de contrat a imposer au modele.
        var effective = template ?? _prompts.Get(_options.PromptName, _options.PromptVersion);

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["question"] = command.Question.Text,
            ["evidence"] = EvidenceFormatter.Format(supplied),
            ["refusal_marker"] = ModelResponseParser.RefusalMarker,
        };

        return effective.Render(values);
    }

    private PromptTemplate? TryLoadTemplate()
    {
        try
        {
            return _prompts.Get(_options.PromptName, _options.PromptVersion);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private string? BuildIndexModelWarning(IndexMetadata metadata)
    {
        if (string.IsNullOrEmpty(metadata.EmbeddingModel))
        {
            // Index jamais construit : ce n'est pas une incoherence de modele, c'est un
            // index vide. La recherche rendra zero resultat et la politique refusera.
            return null;
        }

        if (string.Equals(metadata.EmbeddingModel, _embeddings.Model.Name, StringComparison.Ordinal))
        {
            return null;
        }

        return $"L'index a été construit avec le modèle d'embeddings « {metadata.EmbeddingModel} » " +
               $"alors que la question est encodée avec « {_embeddings.Model.Name} ». " +
               "Les scores de similarité n'ont plus de sens : reconstruisez l'index avant d'interpréter ces résultats.";
    }

    private ConfigurationFingerprint BuildConfiguration(IndexMetadata metadata, PromptTemplate? template) =>
        new(
            EmbeddingModel: _embeddings.Model.Name,
            Dimension: _embeddings.Model.Dimension,
            ChunkingStrategyId: metadata.ChunkingStrategyId,
            FilterMode: _options.FilterMode,
            TopK: _options.TopK,
            MinScore: _options.MinScore,
            PromptName: _options.PromptName,
            PromptVersion: _options.PromptVersion,
            PromptFingerprint: template?.Fingerprint ?? string.Empty,
            LlmModelId: _languageModel.ModelId,
            Temperature: _options.Temperature,
            Seed: _options.Seed);

    private RetrievalTrace BuildTrace(
        RetrievalOutcome retrieval,
        IReadOnlyList<ScoredFragment> dropped,
        ConfigurationFingerprint configuration,
        TimeSpan duration,
        string? indexModelWarning) =>
        new(
            CandidatesFromIndex: retrieval.FromIndex.Count,
            CandidatesAfterAccessFilter: retrieval.AfterAccessFilter.Count,
            FilterMode: _options.FilterMode,
            FromIndex: retrieval.FromIndex,
            Supplied: retrieval.AfterAccessFilter,
            DroppedByAccessFilter: dropped,
            Configuration: configuration,
            Duration: duration,
            IndexModelWarning: indexModelWarning);

    /// <summary>Ce que l'index avait trouve et que le demandeur n'a pas eu le droit de voir.</summary>
    private static IReadOnlyList<ScoredFragment> ComputeDropped(
        IReadOnlyList<ScoredFragment> fromIndex,
        IReadOnlyList<ScoredFragment> supplied)
    {
        if (fromIndex.Count == 0)
        {
            return Array.Empty<ScoredFragment>();
        }

        var kept = new HashSet<ScoredFragment>(supplied);
        var dropped = new List<ScoredFragment>(fromIndex.Count);

        foreach (var scored in fromIndex)
        {
            if (!kept.Contains(scored))
            {
                dropped.Add(scored);
            }
        }

        return dropped;
    }

    private static IReadOnlyList<EvidenceFragment> ToFragments(IReadOnlyList<ScoredFragment> scored)
    {
        if (scored.Count == 0)
        {
            return Array.Empty<EvidenceFragment>();
        }

        var fragments = new List<EvidenceFragment>(scored.Count);
        foreach (var item in scored)
        {
            fragments.Add(item.Fragment);
        }

        return fragments;
    }
}
