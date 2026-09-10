# Le contrat du port de langage : ou vit chaque clause

Ce document etablit, fichier par fichier et ligne par ligne, ce que le systeme exige de
fait du modele de langue. Il decrit l'etat du depot. Il ne propose rien.

Convention de marquage, appliquee a chaque affirmation :

- **[MESURE]** — verifiable dans le code, avec la reference donnee.
- **[INFERE]** — interpretation, la base d'appui est nommee.
- **[HYPOTHESE]** — croyance non testee.

Les numeros de ligne renvoient a l'etat du depot au moment de la redaction.

---

## 0. Point de depart : les trois regimes observes

L'utilisateur a mesure trois modeles et obtenu trois regimes.

| modele | regime | ce qui l'attrape |
| --- | --- | --- |
| `llama3.2:3b` | honore le contrat | rien, interchangeable |
| `llama3.2:1b` | ne cite pas | refus type du Domain |
| `qwen3:4b` | ne termine pas | delai, code 2 |

Ces trois regimes sont attrapes par trois mecanismes qui ne vivent pas dans la meme
couche, ne se declenchent pas au meme moment, et ne produisent pas le meme genre de
sortie. **[MESURE]** — la demonstration occupe les sections 1 a 3.

Le document ne se limite pas a ces trois cas. Quinze obligations ont ete recensees.

---

## 1. Tableau de synthese

Colonnes : **Couche** = ou la clause est ecrite. **Nature** = regle nommee (explicite) ou
consequence de bord (implicite). **Sanction** = ce qui arrive quand la clause est violee.
**Domain seul** = testable sans reseau, sans cle, sans modele.

| # | Obligation | Couche ou elle est ecrite | Nature | Sanction | Domain seul |
| --- | --- | --- | --- | --- | --- |
| 1 | Produire un texte non vide | Domain | Explicite | Refus `ModelProducedEmptyAnswer` | **oui** |
| 2 | Citer au moins une source | Domain (deux fois) | Explicite | Refus `ModelProducedNoCitation` ; `DomainException` en secours | **oui** |
| 3 | Ne citer que des identifiants presents dans les extraits fournis | Domain | Explicite | Refus `ModelCitedUnknownDocument` | **oui** |
| 4 | Ne pas citer un document interdit au demandeur | Domain | Explicite | Refus `ModelCitedForbiddenDocument` | **oui** |
| 5 | Emettre le marqueur de refus SEUL | Gabarit + Application + Domain | Explicite, eclatee en trois | Requalification : le refus mal forme devient `ModelProducedNoCitation` | **partiel** |
| 6 | Respecter le format `[identifiant]` | Application (crochets) + Domain (contenu) | Explicite | Silence : l'identifiant mal forme est ignore, puis cascade en refus | **partiel** |
| 7 | Ne citer une source qu'une fois | Gabarit ; tolere en Application et Domain | Explicite au gabarit, tolerante ailleurs | Aucune : le doublon est absorbe | **oui** (la tolerance) |
| 8 | N'utiliser que les extraits fournis, pas ses connaissances | Gabarit uniquement | Explicite au gabarit | **Aucune** | **non** |
| 9 | Repondre en francais | Gabarit uniquement | Explicite au gabarit | **Aucune** | **non** |
| 10 | Tenir en une ligne / trois phrases / soixante mots | Gabarit uniquement | Explicite au gabarit | **Aucune** | **non** |
| 11 | Ne pas recopier l'identifiant fictif de l'exemple | Gabarit ; rattrape par le Domain | Explicite au gabarit, implicite au Domain | Refus `ModelCitedUnknownDocument` | **oui** (par ricochet) |
| 12 | Tenir dans un budget de jetons | Application (valeur) + Infrastructure (transmission) | Explicite | Troncature silencieuse, puis jugement de ce qui reste | **non** |
| 13 | Tenir dans un delai | Infrastructure + configuration | Implicite | `LanguageModelException`, arret du programme, code 2 | **non** |
| 14 | Ne pas emettre de brouillon de raisonnement | Infrastructure uniquement | Implicite | Nettoyage si la balise fermante existe ; sinon degats non rattrapes | **non** |
| 15 | Annoncer son identifiant (`ModelId`) | Port (Application) | Explicite | Aucune verification ; empreinte de configuration faussee | **non** |

Lecture du tableau. Quatre obligations sur quinze sont dans le Domain et testables hors
ligne (1 a 4). Quatre sont ecrites uniquement dans un fichier markdown de gabarit et rien
ne les fait respecter (8, 9, 10, et 7 pour partie). Deux vivent hors de toute couche
metier, dans la configuration et la plomberie HTTP (12, 13). **[MESURE]** — voir les
sections par clause.

---

## 2. Une section par clause

### Clause 1 — Produire un texte non vide

**Ou.** `src/AssistantQR.Domain/Policies/AnswerPolicy.cs:85-91`.

```csharp
// 4. Le modèle n'a rien produit d'exploitable.
if (draft is null || string.IsNullOrWhiteSpace(draft.Text))
{
    return Refuse(
        RefusalReason.ModelProducedEmptyAnswer,
        "Le modèle n'a produit aucun texte exploitable à partir des extraits fournis.");
}
```

Second lieu, en garde-fou de type : `src/AssistantQR.Domain/Answers/Answer.cs:36-40` leve
`DomainException("Une réponse ne peut pas avoir un texte vide.")`.

Amont, en Application : `src/AssistantQR.Application/Model/ModelResponseParser.cs:67-70`
traduit `null`, vide ou blanc en `DraftAnswer.Empty`.

**Couche.** Domain, avec une traduction en Application.

**Nature.** Explicite. C'est la regle 4 sur huit, numerotee dans le code.

**Qui fait respecter, et que se passe-t-il.** `AnswerPolicy.Decide` rend un
`AnswerOutcome.Refused(ModelProducedEmptyAnswer, ...)`. La CLI l'affiche sous « Refus »
et rend le code 0 — `src/AssistantQR.Cli/Commands/ExitCodes.cs:16`, commentaire :
« Un refus de repondre n'est PAS une erreur ».

**Domain seul : oui.** Tests : `Decide_TexteDuModeleVideOuBlanc_RefuseModelProducedEmptyAnswer`
et `Decide_BrouillonNul_RefuseModelProducedEmptyAnswer`
(`tests/AssistantQR.Domain.Tests/AnswerPolicyTests.cs:203` et `:213`).

---

### Clause 2 — Citer au moins une source

**Ou, premier lieu.** `src/AssistantQR.Domain/Policies/AnswerPolicy.cs:93-100`.

```csharp
// 5. REGLE METIER 1 : pas de citation, pas de réponse.
var citedIds = draft.CitedDocumentIds ?? Array.Empty<DocumentId>();
if (citedIds.Count == 0)
{
    return Refuse(
        RefusalReason.ModelProducedNoCitation,
        "Le modèle a rédigé un texte sans citer la moindre source : une réponse non sourcée est rejetée.");
}
```

**Ou, second lieu.** `src/AssistantQR.Domain/Answers/Answer.cs:42-46`. Le constructeur est
prive ; `Answer.Create` leve si la liste de citations est nulle ou vide. La regle est donc
portee par le TYPE, pas seulement par la politique.

**Couche.** Domain, deux fois, a deux niveaux differents : une decision (refus motive) et
une invariante de construction (exception).

**Nature.** Explicite. Le commentaire d'en-tete d'`AnswerPolicy.cs:4-6` la nomme
« regle metier 1 ».

**Qui fait respecter, et que se passe-t-il.** Chemin nominal : refus motive, code 0.
Chemin de secours : si un appelant tentait de construire une reponse sans citation,
`DomainException`, traduite par `Program.Explain` en « Donnee refusee par le Domain : … »
(`src/AssistantQR.Cli/Program.cs:131`), code 2.

**Domain seul : oui.** Tests : `Decide_TexteSansAucuneCitation_RefuseModelProducedNoCitation`
et `Decide_ListeDeCitationsNulleDansLeBrouillon_RefuseModelProducedNoCitation`
(`AnswerPolicyTests.cs:258` et `:271`).

**C'est cette clause qui attrape `llama3.2:1b`.** **[INFERE]** — d'apres le regime decrit
par l'utilisateur (« ne cite pas ») et le fait qu'aucune autre clause ne produise un refus
sur un texte non vide sans crochets.

---

### Clause 3 — Ne citer que des identifiants presents dans les extraits fournis

**Ou.** `src/AssistantQR.Domain/Policies/AnswerPolicy.cs:102-118`.

```csharp
// 6. REGLE METIER 2 : un identifiant cité qui n'apparait dans aucun extrait
//    fourni est une invention pure du modèle.
```

L'ensemble de reference est `suppliedEvidence`, c'est-a-dire exactement ce que
l'orchestrateur a montre au modele — `AnswerQuestionUseCase.cs:157-162`, commentaire :
« Les fragments soumis au juge sont EXACTEMENT ceux qui ont été montres au modele ».

**Couche.** Domain.

**Nature.** Explicite. Regle 6 sur huit.

**Qui fait respecter, et que se passe-t-il.** Refus `ModelCitedUnknownDocument`, avec la
citation fautive nommee dans l'explication. Code 0.

**Domain seul : oui.** Tests : `Decide_IdentifiantCiteInexistant_RefuseModelCitedUnknownDocument`
(`AnswerPolicyTests.cs:285`) et
`Decide_IdentifiantExistantDansLeCorpusMaisNonFourniEnExtrait_RefuseModelCitedUnknownDocument`
(`:306`). Le second est le plus interessant pour un cours : un identifiant REEL du corpus,
mais non montre a cette question-la, est traite comme une invention.

---

### Clause 4 — Ne pas citer un document interdit au demandeur

**Ou.** `src/AssistantQR.Domain/Policies/AnswerPolicy.cs:120-136`, appuye sur
`src/AssistantQR.Domain/Policies/AccessPolicy.cs:19-21` (`Readable`).

**Couche.** Domain, en deux fichiers du meme dossier `Policies`.

**Nature.** Explicite. Regle 7 sur huit, et « regle metier 3 » dans le commentaire
d'`AccessPolicy.cs:7`.

**Qui fait respecter, et que se passe-t-il.** Refus `ModelCitedForbiddenDocument`. Le cas
n'est atteignable qu'en post-filtrage : en pre-filtrage l'index n'a jamais rendu le
fragment. **[INFERE]** — d'apres `RetrievalStrategyFactory` et le nom du test
`Decide_LeModeleCiteUnDocumentInterditQuIlAVuEnPostFiltrage_RefuseModelCitedForbiddenDocument`.

**Domain seul : oui.** Tests : celui cite ci-dessus (`AnswerPolicyTests.cs:373`) et
`Decide_LeModeleNeCiteQueLeDocumentInterdit_RefuseModelCitedForbiddenDocument` (`:413`).

---

### Clause 5 — Emettre le marqueur de refus SEUL, et pas apres une reponse

C'est la clause la plus eclatee du depot. Elle vit a trois endroits, dans trois couches,
et chacun n'en tient qu'un tiers.

**Tiers 1 — l'ordre, dans le gabarit.** `prompts/answer-with-citations@1.0.0.md:36-39` :

> « Si les extraits ne permettent pas de répondre […] Écris alors {{refusal_marker}}
> seul, sur une ligne, sans phrase d'excuse, sans citation et sans rien ajouter d'autre. »

La version 1.2.0 durcit l'ordre et le repete quatre fois — lignes 11-13, 45-51, 55-56, et
81-82 (« Rappel final : une réponse OU le marqueur de refus, jamais les deux »).

**Tiers 2 — la reconnaissance, en Application.**
`src/AssistantQR.Application/Model/ModelResponseParser.cs:104-123`. La methode `IsRefusal`
retire les marqueurs de citation, retire TOUS les blancs, puis compare au marqueur
`AUCUNE_REPONSE` (`:27`), insensible a la casse.

**Tiers 3 — le sens, dans le Domain.**
`src/AssistantQR.Domain/Policies/AnswerPolicy.cs:77-83`, regle 3, evaluee AVANT le test du
texte vide. Le commentaire `:62-72` explique pourquoi la distinction vit la et pas dans
l'adaptateur. Le vehicule est le drapeau `DraftAnswer.Declined`
(`src/AssistantQR.Domain/Answers/DraftAnswer.cs:26`), qui porte le sens sans porter la
formulation.

**Nature.** Explicite dans les trois lieux. Mais ce sont trois clauses differentes : un
ordre, une reconnaissance de forme, une qualification. **[INFERE]** — d'apres la lecture
des trois fichiers et l'ADR `docs/adr/0006-refus-delibere-et-sortie-vide.md`, qui organise
explicitement ce partage en trois (« Trois pièces, une par couche »).

**Que se passe-t-il quand la clause est violee.** Le point est fin et documente. Un modele
qui ecrit `AUCUNE_REPONSE.` avec un point final n'est PAS reconnu comme declarant. Son
texte suit le chemin ordinaire et se fait refuser a la regle 5, faute de citation. Il est
donc classe `ModelProducedNoCitation` alors qu'il voulait refuser. L'ADR 0006 assume ce
choix, section « Ce que le dépôt paie » (`docs/adr/0006-…:109-115`). **[MESURE]**

**Domain seul : partiel.**

- Le SENS est testable dans le Domain : `Decide_RefusDeclareParLeModele_RefuseModelDeclinedToAnswer`
  (`AnswerPolicyTests.cs:137`), `Decide_RefusDeclareAvecDuTexte_RefuseQuandMemeModelDeclinedToAnswer`
  (`:153`).
- La RECONNAISSANCE n'est pas testable dans le Domain : elle est en Application. Tests
  correspondants : `Parse_RefusalMarker_ProducesADeclinedDraft`,
  `Parse_EmptyResponse_ProducesANonDeclinedDraft`,
  `Parse_RefusalMarkerWithExtraPunctuationOrWords_IsNotADeclaration`
  (`tests/AssistantQR.Application.Tests/Model/ModelResponseParserTests.cs:150`, `:163`, `:179`).
- L'ORDRE, dans le gabarit markdown, n'est teste nulle part comme ordre. Un seul test
  verifie que la constante et les gabarits s'accordent :
  `RefusalMarker_IsTheValueImposedByThePromptTemplates` (`ModelResponseParserTests.cs:130`).
  **[MESURE]**

---

### Clause 6 — Respecter le format `[identifiant]`

**Ou, la forme.** `src/AssistantQR.Application/Model/ModelResponseParser.cs:29-31` :

```csharp
private static readonly Regex CitationPattern =
    new(@"\[([A-Za-z0-9][A-Za-z0-9_\-]{0,127})\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
```

**Ou, le contenu.** `src/AssistantQR.Domain/Documents/DocumentId.cs:22-47` : non vide,
128 caracteres au plus, aucun espace. Le commentaire `:3-8` dit pourquoi : « cet
identifiant est repris tel quel dans les citations en ligne `[identifiant]` imposees au
modele de langue ».

**Ou, le pendant en sortie.** `src/AssistantQR.Application/Model/EvidenceFormatter.cs:43-47`
ecrit l'en-tete `[identifiant] Titre (niveau)`. Le commentaire `:6-9` nomme la parite :
« Ce formateur est le jumeau de `ModelResponseParser` ».

**Couche.** Application pour les crochets, Domain pour le contenu, gabarit pour l'ordre
(regles 3 et 4 des trois gabarits `answer-with-citations`).

**Nature.** Explicite.

**Qui fait respecter, et que se passe-t-il.** Personne ne leve. Un identifiant mal forme
est ignore en silence — `ModelResponseParser.cs:89-98`, le `catch (DomainException)` avec
le commentaire « du bruit de generation, pas une erreur du systeme ». La sanction n'arrive
qu'en cascade : si aucune citation exploitable ne subsiste, la clause 2 refuse.
**[MESURE]**

**Domain seul : partiel.** Le format de l'identifiant, oui —
`tests/AssistantQR.Domain.Tests/DocumentIdTests.cs`. La reconnaissance des crochets, non :
elle est en Application, test `Parse_MalformedCitationMarker_IsSilentlyIgnored`
(`ModelResponseParserTests.cs:99`).

---

### Clause 7 — Ne citer une source qu'une fois

**Ou, l'ordre.** `prompts/answer-with-citations@1.2.0.md:64-66`, regle 10 : « Un même
identifiant ne se cite qu'une fois. » Absent des versions 1.0.0 et 1.1.0. Le gabarit 1.1.0
en donne une variante differente en regle 6 (`:45-46`) : ne pas regrouper plusieurs
identifiants sur une meme affirmation.

**Ou, la tolerance.** Trois dedoublonnages successifs, aucun refus :

1. `ModelResponseParser.cs:79-87` — `HashSet` sur les candidats, un identifiant repete
   n'est ajoute qu'une fois.
2. `AnswerPolicy.cs:142-148` — `alreadyCited`, commentaire : « citer deux fois la meme
   source n'ajoute pas de source ».
3. `Answer.cs:60-64` — leve `DomainException` seulement sur un doublon du couple
   (document, fragment), ce que les deux etapes precedentes ont deja rendu impossible.

**Nature.** Explicite au gabarit (1.2.0 seulement), tolerante partout ailleurs.

**Qui fait respecter, et que se passe-t-il.** Rien ne sanctionne. Le doublon est absorbe
avant d'atteindre le juge. **[MESURE]**

**Domain seul : oui**, mais ce qui est teste est la TOLERANCE, pas l'obligation. Le depot
verifie qu'un doublon ne casse rien — `Parse_RepeatedCitation_IsDeduplicated`
(`ModelResponseParserTests.cs:47`).

---

### Clause 8 — N'utiliser que les extraits fournis, pas ses connaissances generales

**Ou.** Gabarit uniquement. `prompts/answer-with-citations@1.0.0.md:25-27`, regle 2 :

> « Appuie-toi UNIQUEMENT sur les extraits ci-dessus. Tes connaissances générales sur les
> bibliothèques, les médiathèques ou la fonction publique ne comptent pas : si un fait
> n'est pas écrit dans un extrait, il n'existe pas pour cette réponse. »

Meme regle en 1.2.0 (`:34-36`).

**Couche.** Gabarit de prompt. Rien en Domain, rien en Application, rien en Infrastructure.

**Nature.** Explicite au gabarit, absente du code.

**Qui fait respecter, et que se passe-t-il.** **Personne.** `AnswerPolicy.Decide` ne
compare jamais `draft.Text` au texte des fragments. Les seules comparaisons portent sur
les IDENTIFIANTS (`AnswerPolicy.cs:104-136`). Un texte entierement invente qui se termine
par un identifiant valide et lisible franchit les huit regles et devient une
`AnswerOutcome.Answered`. **[MESURE]** — verifiable en lisant les huit branches de
`Decide` : aucune ne lit le contenu des extraits.

Ce que le systeme garantit reellement est donc plus etroit que ce que le gabarit demande :
il garantit qu'une reponse est ACCOMPAGNEE d'une source existante et lisible, pas qu'elle
en DECOULE. **[INFERE]** — d'apres l'absence de toute comparaison de contenu dans `Decide`.

**Domain seul : non.** Aucun test ne l'atteste, et il ne peut pas y en avoir : la clause
n'existe pas dans le code.

---

### Clause 9 — Repondre en francais

**Ou.** Gabarit uniquement, regle 1 des trois versions :

- `prompts/answer-with-citations@1.0.0.md:24` — « Réponds en français, dans une langue
  simple et directe. Cinq phrases au maximum. »
- `prompts/answer-with-citations@1.1.0.md:33` — « Réponds en français. TROIS phrases au
  maximum, soixante mots au maximum en tout. »
- `prompts/answer-with-citations@1.2.0.md:32-33` — « Réponds en français […] Trois phrases
  au maximum, sur une seule ligne. »

**Couche.** Gabarit de prompt.

**Nature.** Explicite au gabarit, absente du code.

**Qui fait respecter, et que se passe-t-il.** **Personne.** Aucune detection de langue
n'existe dans le depot. Une reponse anglaise correctement citee est acceptee et rendue a
l'usager. **[MESURE]**

Point connexe, et c'est le seul endroit ou la langue a une consequence technique :
`OllamaLanguageModel.cs:98-104` documente qu'un brouillon de raisonnement arrive « en
anglais » et prenait la place de la reponse. Le remede n'est pas une verification de
langue, c'est `think=false` — voir clause 14.

**Domain seul : non.** Aucun test.

---

### Clause 10 — Tenir en une ligne, trois phrases, soixante mots

**Ou.** Gabarit uniquement, et la contrainte change a chaque version — c'est meme la seule
chose que les trois versions font varier de facon marquee :

| version | contrainte de longueur | reference |
| --- | --- | --- |
| 1.0.0 | cinq phrases au maximum | `answer-with-citations@1.0.0.md:24` |
| 1.1.0 | trois phrases, soixante mots, une affirmation par ligne | `@1.1.0.md:33` et `:24-26` |
| 1.2.0 | une seule ligne, trois phrases, aucune ligne vide | `@1.2.0.md:11-13`, `:32-33`, `:84-86` |

**Couche.** Gabarit de prompt.

**Nature.** Explicite au gabarit, absente du code.

**Qui fait respecter, et que se passe-t-il.** **Personne.** Aucune verification de
longueur, de nombre de phrases ni de nombre de lignes. La seule troncature du systeme est
celle des EXTRAITS recopies dans les citations — `PipelineOptions.ExcerptLength`
(`:47`) puis `TextExcerpt.Shorten` (`Citation.cs:25`) — et elle ne touche pas au texte du
modele. **[MESURE]**

**Domain seul : non.** Aucun test.

---

### Clause 11 — Ne pas recopier l'identifiant fictif de l'exemple

**Ou, l'ordre.** `prompts/answer-with-citations@1.2.0.md:57-59`, regle 8 :

> « La règle 3 donne une forme, jamais un contenu : ni sa phrase d'exemple, ni
> l'identifiant fictif qu'elle porte ne se recopient. »

L'exemple en question est `[identifiant-de-l-extrait]` (`@1.0.0.md:30`, `@1.2.0.md:38-39`).

**Couche.** Gabarit pour l'ordre. Domain pour la sanction, par ricochet.

**Nature.** Explicite au gabarit. Implicite au Domain : la clause 3 attrape ce cas sans
avoir ete ecrite pour lui.

**Qui fait respecter, et que se passe-t-il.** `AnswerPolicy` regle 6. La chaine
`identifiant-de-l-extrait` ne figure dans aucun extrait fourni, donc refus
`ModelCitedUnknownDocument`. **[INFERE]** — d'apres la forme de l'identifiant fictif, qui
passe le regex de citation (`ModelResponseParser.cs:30`, lettres et tirets uniquement)
mais ne peut pas etre dans `suppliedIds`.

C'est le seul cas du depot ou une regle de redaction du gabarit se trouve appuyee par une
regle du Domain — et l'appui est accidentel, pas concu. **[INFERE]** — d'apres l'absence de
tout commentaire liant les deux.

**Domain seul : oui, par ricochet.** Le test qui l'atteste est celui de la clause 3,
`Decide_IdentifiantCiteInexistant_RefuseModelCitedUnknownDocument`.

---

### Clause 12 — Tenir dans un budget de jetons

**Ou, la valeur.** `src/AssistantQR.Application/Configuration/PipelineOptions.cs:26-27` :

```csharp
/// <summary>Longueur maximale de la completion.</summary>
public int MaxTokens { get; init; } = 600;
```

**Ou, la validation du reglage.** `PipelineOptions.cs:78-82` — `MaxTokens < 1` leve
`InvalidOperationException`.

**Ou, la transmission.** `AnswerQuestionUseCase.cs:149-153` :

```csharp
var completion = await _languageModel
    .CompleteAsync(
        new LlmRequest(prompt, _options.Temperature, _options.Seed, _options.MaxTokens),
        cancellationToken)
```

**Ou, l'application effective.** `src/AssistantQR.Infrastructure/LanguageModels/OllamaLanguageModel.cs:177` :

```csharp
[property: JsonPropertyName("num_predict")] int NumPredict,
```

**Ou, la configuration.** `src/AssistantQR.Cli/appsettings.json:54` (`"MaxTokens": 600`) et
`src/AssistantQR.Infrastructure/Configuration/PipelineSection.cs:35`. Il n'existe PAS
d'option de ligne de commande `--max-tokens` : la liste des surcharges CLI est
`src/AssistantQR.Cli/Configuration/CliConfiguration.cs:45-51`, et `MaxTokens` n'y figure
pas. **[MESURE]**

**Couche.** Application (la valeur, dans l'empreinte de configuration), Infrastructure (la
transmission au fournisseur), configuration (le reglage).

**Nature.** Explicite : c'est un parametre nomme du port, `LlmRequest.MaxTokens`.

**Qui fait respecter, et que se passe-t-il.** Le fournisseur, pas le systeme. Ollama arrete
la generation ; la sortie tronquee revient telle quelle et est jugee par le Domain. Si la
troncature a coupe avant la citation, le resultat est un refus `ModelProducedNoCitation`
qui ressemble a la clause 2 mais n'a pas la meme cause. **[INFERE]** — d'apres
`OllamaLanguageModel.cs:129-133` (« Une reponse vide n'est PAS une panne d'infrastructure »)
et le commentaire `:110-114` sur le brouillon qui depasse le budget.

Les deux doublures IGNORENT ce parametre. `ExtractiveLanguageModel.CompleteAsync`
(`:94-132`) ne lit que `request.Prompt` ; sa borne de longueur est la constante
`MaxFragmentLength = 400` (`:62`). `ReplayLanguageModel.CompleteAsync` (`:110-…`) ne lit
que `request.Prompt` egalement. **[MESURE]**

**Domain seul : non.** Le Domain ne connait pas `MaxTokens` : ce symbole n'apparait dans
aucun fichier de `src/AssistantQR.Domain/`. Deux tests existent, tous deux en Application,
et aucun ne teste le RESPECT du budget :

- `Validate_MaxTokensBelowOne_Throws`
  (`tests/AssistantQR.Application.Tests/Configuration/PipelineOptionsTests.cs:89`) — teste
  la validite du reglage.
- `ExecuteAsync_TemperatureSeedAndMaxTokens_AreForwardedToTheLanguageModel`
  (`tests/AssistantQR.Application.Tests/UseCases/AnswerQuestionUseCaseTests.cs:325`) —
  teste la TRANSMISSION, avec l'assertion `Assert.Equal(99, request.MaxTokens)`.

---

### Clause 13 — Tenir dans un delai

C'est la clause sur laquelle porte le constat de l'utilisateur. Voici son trajet complet.

**Ou, la valeur.** `src/AssistantQR.Infrastructure/Configuration/LanguageModelOptions.cs:33-34` :

```csharp
/// <summary>Delai d'attente. Une generation sur processeur peut etre longue.</summary>
public int TimeoutSeconds { get; set; } = 180;
```

**Ou, l'application.** `src/AssistantQR.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs:213-221` :

```csharp
services.AddHttpClient(OllamaClientName, client =>
{
    client.BaseAddress = ollamaUri;

    // Le delai est genereux : sur processeur, une generation de six cents jetons
    // se compte en dizaines de secondes. Un delai serre transformerait une lenteur
    // attendue en panne inexplicable.
    client.Timeout = TimeSpan.FromSeconds(Math.Max(1, options.LanguageModel.TimeoutSeconds));
});
```

**Ou, la traduction en panne.** `src/AssistantQR.Infrastructure/Http/JsonHttp.cs:93-102` :

```csharp
catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
{
    throw context.CreateException(
        $"{context.ServiceLabel} n'a pas repondu dans le delai imparti " +
        $"({client.Timeout.TotalSeconds:0} s) sur {Describe(client)}. " +
        "Augmente le delai dans la configuration, ou choisis un modele plus petit.",
        exception);
}
```

Le type leve est `LanguageModelException`, fabrique par le `JsonHttpContext` monte dans
`OllamaLanguageModel.cs:62-79`.

**Ou, la sanction visible.** `src/AssistantQR.Cli/Program.cs:112-114` traduit
`LanguageModelException` en message, puis `:62-66` rend `ExitCodes.Execution`, c'est-a-dire
**2** (`ExitCodes.cs:22`).

**Ou, le reglage.** `src/AssistantQR.Cli/appsettings.json:45` (`"TimeoutSeconds": 180`) et
la variable d'environnement `ASSISTANTQR_LANGUAGEMODEL__TIMEOUTSECONDS`. Il n'existe PAS
d'option de ligne de commande : `CliConfiguration.OverrideOptions` (`:45-51`) ne contient
pas `timeout`. **[MESURE]**

**Couche.** Infrastructure et configuration. **Rien dans le Domain. Rien dans
l'Application. Rien dans le port.** Le symbole `Timeout` n'apparait ni dans
`src/AssistantQR.Domain/`, ni dans `src/AssistantQR.Application/`. **[MESURE]**

**Nature.** Implicite. Aucune regle nommee n'exige du modele qu'il termine. Le delai est
une propriete du transport HTTP qui se trouve avoir un effet de bord de clause.
**[INFERE]** — d'apres le fait que la valeur vit dans un objet de configuration
d'infrastructure et est consommee par `HttpClient.Timeout`, un mecanisme de transport.

**Que se passe-t-il quand elle est violee.** C'est le point structurant. Le depassement de
delai ne produit **aucun `AnswerOutcome`**. La chaine s'interrompt dans `JsonHttp.SendAsync`,
avant le retour d'`OllamaLanguageModel.CompleteAsync`, donc avant
`ModelResponseParser.Parse` et donc avant `AnswerPolicy.Decide`. Il n'y a ni reponse, ni
refus : il y a une exception, un message, et le code 2. **[MESURE]** — d'apres l'ordre des
appels dans `AnswerQuestionUseCase.cs:149-162`.

Contraste avec les clauses 1 a 4 : celles-la produisent un `AnswerOutcome.Refused`, une
cause tiree d'une enumeration fermee de sept valeurs, une explication en francais, et le
code **0**. Le delai produit une exception, hors de l'enumeration, et le code **2**.
**[MESURE]**

L'ADR 0006 traite ce point de front et l'assume, section « Alternatives écartées »
(`docs/adr/0006-…:180-186`) :

> « Un `RefusalReason` par cause technique (`ModelTimedOut`, `ModelTruncated`,
> `ProviderUnavailable`). Tentant, et faux ici : ces causes ne sont pas des décisions du
> Domain, ce sont des incidents que les adaptateurs traduisent déjà en exceptions typées
> avec un message actionnable. »

**Domain seul : non.** Le Domain n'a aucun moyen d'exprimer ni d'observer un delai.
**Aucun test du depot ne couvre le depassement de delai du modele de langue.** La
recherche de `Timeout` dans `tests/AssistantQR.Infrastructure.Tests/DependencyInjection/ServiceCollectionExtensionsTests.cs`
ne rend rien ; les tests d'integration `OllamaLanguageModelIntegrationTests` couvrent le
serveur injoignable et le modele absent, pas le delai. **[MESURE]**

**C'est cette clause qui attrape `qwen3:4b`.** **[INFERE]** — d'apres le regime decrit par
l'utilisateur (« delai, code 2 ») et le fait que le code 2 ne peut venir que du
`catch (Exception)` de `Program.Main:62-66`.

---

### Clause 14 — Ne pas emettre de brouillon de raisonnement

**Ou, la prevention.** `src/AssistantQR.Infrastructure/LanguageModels/OllamaLanguageModel.cs:97-117`.
Le champ `think` est envoye a `false`, inconditionnellement. Le commentaire est le plus
long du fichier et enonce trois degats mesures :

> « - la reponse rendue est le brouillon, en anglais, et non la reponse ;
> - le brouillon recopie le bloc d'extraits, donc `ModelResponseParser` y trouve des
> [identifiants] et fabrique des citations a partir d'un raisonnement, pas d'une reponse ;
> - le cout : 155 s au lieu de 1 s pour la meme question. »

Le meme commentaire (`:106-114`) enregistre que l'effet du champ DEPEND DU MODELE : sur
`granite4.2:3b` il supprime le raisonnement (10 jetons, 1,0 s au lieu de 155 s) ; sur
`qwen3:4b` il ne supprime rien — 245 jetons avec comme sans — il deplace seulement le
brouillon du champ `thinking` vers `response`. **[MESURE]** — ces chiffres sont dans le
code, ils ne sont pas remesures ici.

**Ou, le rattrapage.** `OllamaLanguageModel.cs:155-162` :

```csharp
private static string StripReasoning(string raw)
{
    const string closing = "</think>";

    var last = raw.LastIndexOf(closing, StringComparison.OrdinalIgnoreCase);

    return last < 0 ? raw : raw[(last + closing.Length)..].TrimStart();
}
```

**Couche.** Infrastructure, uniquement. Le port ne mentionne rien, aucun gabarit
n'interdit le raisonnement, le Domain n'en a pas connaissance. **[MESURE]**

**Nature.** Implicite. C'est une consequence de bord d'un contrat de fournisseur — le champ
`think` de l'API Ollama — devenue une obligation de fait.

**Qui fait respecter, et que se passe-t-il.** L'adaptateur, et seulement si la balise
fermante est presente. Le commentaire `:110-114` decrit le cas non rattrapable :

> « Et quand le brouillon depasse le budget de jetons, la balise fermante n'arrive jamais,
> il n'y a plus rien a rattraper, et le refus du Domain est la seule issue correcte. »

Cette clause est donc couplee a la clause 12 : le budget de jetons determine si le
rattrapage est possible. **[MESURE]**

**Domain seul : non.** Aucun test du depot ne couvre `StripReasoning` ni le champ `think` :
la recherche de `think` et de `StripReasoning` dans `tests/` ne rend aucun resultat.
**[MESURE]**

---

### Clause 15 — Annoncer son identifiant

**Ou.** `src/AssistantQR.Application/Ports/ILanguageModel.cs:28-29` :

```csharp
/// <summary>Identifiant du modele, tel qu'il figurera dans les traces et les instantanes.</summary>
string ModelId { get; }
```

Consomme en `AnswerQuestionUseCase.cs:231` (`LlmModelId: _languageModel.ModelId`), qui
alimente `ConfigurationFingerprint`.

**Couche.** Port, donc Application. La valeur est fournie par l'Infrastructure.

**Nature.** Explicite. Le `remarks` du port `:22-24` justifie sa presence : « Une derive de
reponses apres un changement de modele doit être attribuable a ce changement ».

**Qui fait respecter, et que se passe-t-il.** Personne ne verifie. Trois comportements
differents coexistent :

- `OllamaLanguageModel.cs:136` prefere le nom rendu par le serveur, avec repli sur le nom
  configure : `string.IsNullOrWhiteSpace(response.Model) ? ModelId : response.Model`.
- `ExtractiveLanguageModel.cs:92` rend une constante, `"extractive-fake"`.
- `ReplayLanguageModel.cs:60` rend `"replay"`, mais son commentaire `:27-28` precise que
  lors d'un repli sur la doublure, c'est le `ModelId` de la doublure qui est rendu — « la
  trace doit dire quel modele a REELLEMENT parle ».

**[MESURE]** — les trois lignes sont citees.

Le seul garde-fou du depot sur ce terrain ne porte pas sur le modele mais sur les
doublures : `ServiceCollectionExtensions.EnsureNoUnannouncedFakes` (`:300-340`) refuse de
demarrer en profil `local` si une doublure est branchee sans avoir ete demandee. Il
protege l'HONNETETE du montage, pas l'exactitude du `ModelId` rendu. **[INFERE]** — d'apres
la portee du controle, qui lit `options`, pas les completions.

**Domain seul : non.** Le Domain ignore `ModelId`.

---

## 3. Ou vivent les trois regimes, cote a cote

Reprise du tableau initial, complete par les references etablies ci-dessus.

| modele | regime | clause qui l'attrape | couche | forme de la sortie | code de retour |
| --- | --- | --- | --- | --- | --- |
| `llama3.2:3b` | honore le contrat | aucune | — | `AnswerOutcome.Answered` | 0 |
| `llama3.2:1b` | ne cite pas | clause 2 | Domain, `AnswerPolicy.cs:93-100` | `AnswerOutcome.Refused(ModelProducedNoCitation)` | 0 |
| `qwen3:4b` | ne termine pas | clause 13 | Infrastructure, `ServiceCollectionExtensions.cs:220` + `JsonHttp.cs:93-102` | `LanguageModelException` | 2 |

Trois observations factuelles.

**Un.** Les deux clauses qui attrapent quelque chose ne vivent pas dans la meme couche, et
ne sont pas du meme genre. L'une est une regle metier nommee, evaluee par une fonction pure
(`AnswerPolicy.Decide`, aucune I/O, `AnswerPolicy.cs:25`). L'autre est un reglage de
transport (`HttpClient.Timeout`). **[MESURE]**

**Deux.** Les deux violations ne produisent pas le meme genre d'objet. La premiere produit
une valeur du domaine — `AnswerOutcome.Refused`, avec une cause tiree d'une enumeration
fermee de sept valeurs (`RefusalReason.cs`) et une explication en francais. La seconde
produit une exception, hors enumeration, hors instantane. **[MESURE]**

**Trois.** Les deux violations ne sont pas enregistrables de la meme facon. Un refus entre
dans un instantane : `SnapshotEntry` porte le motif, et `CompareSnapshots` sait dire qu'un
motif a change (test `CompareSnapshots_RefusalReasonChanged_ReportsRefusalChanged`,
`tests/AssistantQR.Application.Tests/UseCases/SnapshotTests.cs:252`). Un depassement de
delai interrompt la commande : il n'y a pas d'entree a enregistrer. **[INFERE]** — d'apres
le fait que l'exception remonte avant la construction de `AnswerQuestionResult`.

---

## 4. Ce que l'interface `ILanguageModel` dit, et ce qu'elle tait

Le port complet tient en deux membres — `src/AssistantQR.Application/Ports/ILanguageModel.cs:26-33` :

```csharp
public interface ILanguageModel
{
    string ModelId { get; }

    Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default);
}
```

Et le type d'entree, `src/AssistantQR.Application/Model/LlmRequest.cs:12-17` :

```csharp
public sealed record LlmRequest(
    string Prompt,
    double Temperature,
    int? Seed,
    int MaxTokens,
    IReadOnlyList<string>? Stop = null);
```

### 4.1 Ce qui est visible ET tenu

| element | ou il agit | preuve |
| --- | --- | --- |
| `Prompt` | seul vehicule de la question, des extraits et de toutes les clauses de gabarit | `AnswerQuestionUseCase.cs:147` puis `:151` |
| `ModelId` | entre dans `ConfigurationFingerprint` | `AnswerQuestionUseCase.cs:231` |
| `CancellationToken` | annulation par l'usager (Ctrl+C) | `Program.cs:39-45`, `JsonHttp.cs:93` distingue annulation et delai |

### 4.2 Ce qui est visible mais SANS FORCE

Un parametre qu'un adaptateur peut ignorer sans que rien ne le signale.

**`Temperature` et `Seed`.** `ExtractiveLanguageModel` les ignore, et c'est documente
comme un choix : `ExtractiveLanguageModel.cs:23-29` — « IL IGNORE DELIBEREMENT LA
TEMPERATURE ET LA GRAINE — et c'est exactement ce qui le rend utile. » Verification faite :
sa methode `CompleteAsync` (`:94-132`) ne lit que `request.Prompt`, jamais
`request.Temperature` ni `request.Seed`. Le test
`CompleteAsync_TemperatureEtGraine_SontIgnoreesDeliberement`
(`tests/AssistantQR.Infrastructure.Tests/LanguageModels/ExtractiveLanguageModelTests.cs:210`)
fige ce comportement : deux appels avec des temperatures differentes rendent la meme
chaine. **[MESURE]**

`ReplayLanguageModel` les ignore aussi : `CompleteAsync` (`:110-…`) ne lit que
`request.Prompt`. **[MESURE]**

`OllamaLanguageModel` les transmet sans les reecrire (`:118-122`). Le `remarks` `:20-26`
enonce la raison et la limite : « meme avec temperature 0 et graine fixe, Ollama ne
garantit pas la reproductibilite entre deux versions du serveur ou deux machines ».

Le port declare donc un reglage de determinisme que trois adaptateurs sur trois honorent
differemment : deux l'ignorent, le troisieme le transmet sans pouvoir le garantir.
**[MESURE]**

**`MaxTokens`.** Meme situation : transmis par Ollama en `num_predict` (`:177`), ignore par
les deux doublures. Voir clause 12.

**`Stop`.** Cas particulier : ce parametre est declare dans le port, il a une valeur par
defaut `null`, et **le pipeline ne le renseigne jamais**. La seule construction de
`LlmRequest` dans `src/` est `AnswerQuestionUseCase.cs:151`, qui utilise la forme a quatre
arguments. `OllamaLanguageModel.cs:122` sait le transmettre
(`request.Stop is { Count: > 0 } ? request.Stop : null`), mais recevra toujours `null`.
**[MESURE]** — recherche de `Stop` dans `src/` : trois occurrences pertinentes, aucune
n'ecrit la valeur.

C'est une clause presente dans la signature, implementee cote adaptateur reel, et morte
dans le montage actuel.

### 4.3 Ce qui n'est PAS VISIBLE DU TOUT dans la signature

Aucune de ces obligations n'a de trace dans `ILanguageModel` ni dans `LlmRequest` :

| obligation | ou elle vit reellement |
| --- | --- |
| Citer au moins une source | Domain, `AnswerPolicy.cs:93-100` |
| Ne citer que ce qui a ete montre | Domain, `AnswerPolicy.cs:102-118` |
| Ne pas citer un document interdit | Domain, `AnswerPolicy.cs:120-136` |
| Emettre le marqueur de refus seul | gabarit + `ModelResponseParser.cs:104-123` + `AnswerPolicy.cs:77-83` |
| Format `[identifiant]` | `ModelResponseParser.cs:29-31` + `DocumentId.cs:37-44` |
| Repondre en francais | gabarits uniquement |
| Longueur de la reponse | gabarits uniquement |
| Ne pas utiliser ses connaissances generales | gabarits uniquement |
| **Tenir dans un delai** | `LanguageModelOptions.cs:34` + `ServiceCollectionExtensions.cs:220` |
| Ne pas emettre de brouillon de raisonnement | `OllamaLanguageModel.cs:117` et `:155-162` |
| Niveaux d'acces | `AccessLevel`, `AccessPolicy` — le port n'en sait rien |

**[MESURE]** pour chaque ligne : les references sont celles etablies en section 2.

### 4.4 Ce que le port dit de lui-meme

Le `remarks` du port revendique cette etroitesse — `ILanguageModel.cs:7-20` :

> « Elle est etroite a dessein : une chaine entre, une chaine sort. […] Le modele de langue
> est le seul composant du systeme dont on ne peut pas garantir la sortie. Le confiner
> derriere une interface a une methode, c'est se donner le droit de ne jamais lui faire
> confiance : ce qu'il rend est une PROPOSITION, analysee par le parseur, puis jugee par la
> politique du Domain. Toutes les regles de securite du systeme passent APRES ce port,
> jamais dedans. »

Constat factuel : cette phrase est exacte pour les clauses 1 a 4, qui sont bien evaluees
apres le port. Elle ne couvre pas les clauses 13 et 14, qui sont evaluees AVANT le retour
du port, dans l'adaptateur et dans le transport. **[MESURE]** — d'apres l'ordre des appels
dans `AnswerQuestionUseCase.cs:149-162` et l'emplacement du `catch` dans
`JsonHttp.cs:93-102`.

Autrement dit : le port dit ce qu'il RECOIT (un prompt, quatre reglages) et ce qu'il REND
(un texte, un identifiant, deux compteurs facultatifs). Il ne dit rien de ce qu'il EXIGE.

---

## 5. Questions ouvertes

Cette section expose des tensions. Elle ne tranche pas.

### 5.1 Un budget de temps doit-il etre une clause du port, au meme titre que l'obligation de citer ?

**Position A — le delai est une clause metier, il appartient au contrat.**

Sa forme la plus forte. Une obligation du modele est une obligation, quelle que soit la
technique qui la fait respecter. Le systeme dit a son usager : « je te reponds en citant,
ou je refuse en te disant pourquoi ». Un depassement de delai casse cette promesse, et la
casse de la maniere la plus visible qui soit — l'usager n'obtient ni reponse ni refus, mais
un message technique et un code 2.

Le depot lui-meme a deja tranche ce genre d'arbitrage une fois, en sens inverse. L'ADR 0006
argumente que confondre « le modele a obei » et « le modele n'a rien rendu » detruit une
information avant la frontiere, et que cette destruction est irreversible :

> « Une fois cette traduction faite, l'information n'existe plus : aucun test du Domain,
> aucune politique, aucune regle ajoutee plus tard ne peut la reconstruire, parce qu'elle
> a ete detruite AVANT la frontiere. » (`docs/adr/0006-…:161-165`)

Le meme raisonnement s'applique mot pour mot au delai : il est traduit en exception avant
la frontiere, donc le Domain ne peut plus rien en dire. La consequence est mesurable :
`RefusalReason` compte sept motifs, et le regime observe sur `qwen3:4b` n'en a aucun.
Un instantane enregistre les refus (`SnapshotTests.cs:115`,
`RecordSnapshot_RecordsRefusalsAsFirstClassResults`) mais ne peut pas enregistrer un
depassement de delai — la commande s'arrete avant. Un modele qui echoue par delai est donc,
pour l'outil de comparaison du depot, invisible plutot que mauvais. **[MESURE]**

Argument de symetrie, enfin. Le budget de jetons, lui, EST dans le port (`MaxTokens`).
Jetons et secondes sont deux budgets de la meme generation ; l'un est visible dans la
signature, l'autre pas. Si `MaxTokens` merite d'etre dans `LlmRequest`, la meme raison
vaut pour une echeance. **[INFERE]** — d'apres la presence de `MaxTokens` dans
`LlmRequest.cs:16` et l'absence de tout equivalent temporel.

**Position B — le delai est une propriete de deploiement, il n'a rien a faire dans un
contrat metier.**

Sa forme la plus forte. Un contrat metier enonce ce qui est VRAI d'une reponse acceptable.
« Une reponse cite ses sources » est une propriete de la reponse : on peut la verifier en
regardant la reponse, sans savoir quelle machine l'a produite, ni quand, ni combien de
temps elle a mis. C'est precisement ce qui rend `AnswerPolicy` testable en quelques
millisecondes, hors ligne, avec un resultat toujours identique — la revendication de
`AnswerPolicy.cs:7-10`.

« Une reponse arrive en moins de 180 secondes » n'est pas une propriete de la reponse. La
meme reponse, octet pour octet, satisfait ou viole la clause selon le processeur, la
charge de la machine, la taille du modele et la version du serveur. Une clause dont la
verite depend du materiel n'est pas une invariante : c'est un accord de niveau de service.
Le mettre dans le Domain rendrait le Domain non deterministe, et detruirait la propriete
que tout le depot enseigne.

Le depot a d'ailleurs deja ecrit ce refus. ADR 0006, alternatives ecartees :

> « ces causes ne sont pas des decisions du Domain, ce sont des incidents que les
> adaptateurs traduisent deja en exceptions typees avec un message actionnable. […] Les
> faire entrer dans l'enumeration des refus reviendrait a confondre "le systeme a decide de
> ne pas repondre" et "le systeme n'a pas pu fonctionner". » (`docs/adr/0006-…:180-186`)

Argument de valeur par defaut, enfin. `TimeoutSeconds = 180` (`LanguageModelOptions.cs:34`)
n'a de sens que pour Ollama sur processeur local. Le commentaire du montage le dit
(`ServiceCollectionExtensions.cs:217-219`). Une valeur qui n'a de sens qu'en presence d'un
fournisseur donne et d'un materiel donne est, par definition, un reglage de deploiement.
**[MESURE]**

**Le point ou les deux positions se rencontrent, et qui reste ouvert.** Les deux camps
s'accordent sur un fait : aujourd'hui, la meme notion — ce que le systeme exige du modele —
est ecrite a deux niveaux qui n'ont ni le meme vocabulaire, ni le meme mode de defaillance,
ni le meme code de retour. Ils ne s'accordent pas sur ce qui, de la clause de citation ou
de la clause de delai, se trouve « au mauvais endroit ». La position A dit que le delai est
trop bas. La position B dit que la question est mal posee : ce ne sont pas deux clauses du
meme contrat, ce sont un contrat et une condition d'exploitation, et leur cohabitation a
deux niveaux est normale.

### 5.2 Une obligation ecrite dans un gabarit de prompt markdown non teste est-elle une clause de contrat ou un voeu pieux ?

Les faits d'abord. Quatre obligations (clauses 8, 9, 10, et 7 pour partie) vivent
UNIQUEMENT dans `prompts/*.md`. Aucune n'est verifiee par une ligne de code. Aucune n'a de
test. **[MESURE]**

Ce que le depot teste des gabarits, exhaustivement :

- que le marqueur constant correspond a celui des fichiers —
  `RefusalMarker_IsTheValueImposedByThePromptTemplates` (`ModelResponseParserTests.cs:130`) ;
- que le rendu ne laisse aucun emplacement non resolu — `PromptTemplate.Render`
  (`PromptTemplate.cs:79-84`), teste par `PromptTemplateTests.cs:20` ;
- que le prompt construit porte bien question, extraits et marqueur —
  `ExecuteAsync_NominalPath_ThePromptCarriesQuestionEvidenceAndRefusalMarker`
  (`AnswerQuestionUseCaseTests.cs:108`) ;
- qu'une doublure repond ou refuse avec le vrai gabarit du depot —
  `CompleteAsync_VraiGabaritDuDepot_RepondQuandLExtraitRecoupeEtRefuseSinon`
  (`ExtractiveLanguageModelTests.cs:247`).

Le depot verifie donc la MECANIQUE du gabarit (il se rend, il porte ses trois valeurs, sa
constante s'accorde avec le code), jamais son CONTENU normatif. Aucun test n'affirme
« la regle 1 existe » ou « un modele qui repond en anglais est rejete ». **[MESURE]**

**Position A — c'est une clause de contrat.**

Sa forme la plus forte. Le depot traite deja le gabarit comme un artefact de premiere
classe, et pas comme une chaine perdue au fond d'une methode. Il a un nom, une version, une
empreinte SHA-256 (`PromptTemplate.cs:42-43`, `:92-96`), et cette empreinte entre dans
`ConfigurationFingerprint` (`AnswerQuestionUseCase.cs:230`). Une derive de reponses apres
modification du gabarit est donc attribuable. Le commentaire du type le revendique
(`PromptTemplate.cs:8-13`) : « un prompt est un artefact versionne, nomme et empreinte ».

De plus, une clause du gabarit peut avoir un effet mesurable sans etre verifiee par du
code. Le depot en donne un exemple chiffre : trois versions du gabarit
`answer-with-citations` existent, et la 1.2.0 est decrite comme un
« durcissement […] pour petits modeles locaux » (`@1.2.0.md:4`). Si le contenu du gabarit
etait un voeu pieux, une version 1.2.0 n'aurait pas de raison d'exister. **[INFERE]** —
d'apres l'existence des trois versions et la description de la 1.2.0.

Enfin, l'ADR 0006 qualifie explicitement l'ordre du gabarit de regle metier :

> « Cet ordre n'est pas une astuce de formatage : c'est la regle metier "refuser plutot
> qu'inventer", ecrite en langue naturelle a destination d'un executant non deterministe.
> Un modele qui emet ce marqueur OBEIT. » (`docs/adr/0006-…:14-18`)

**Position B — c'est un voeu pieux.**

Sa forme la plus forte. Une clause de contrat se distingue d'un souhait par une seule
chose : sa violation a une consequence. Les clauses 8, 9 et 10 n'en ont aucune. Un modele
qui repond en anglais, en quinze phrases, a partir de ses connaissances generales, et qui
termine par un identifiant valide, produit une `AnswerOutcome.Answered`. Le systeme la rend
a l'usager sans un mot. **[MESURE]** — d'apres l'absence de toute verification de langue,
de longueur ou de contenu dans les huit branches de `AnswerPolicy.Decide`.

Le depot fait par ailleurs une distinction tres nette, ailleurs, entre ce qu'il exige et ce
qu'il espere. `PromptTemplate.Render` refuse de rendre un gabarit avec un trou dedans, et
le justifie : « un prompt envoye avec un trou dedans produit une reponse plausible et
fausse, c'est-a-dire le pire des bogues » (`PromptTemplate.cs:47-50`). C'est une clause :
elle leve. Les regles 1, 2 et 10 du meme fichier ne levent jamais.

Enfin, la clause 11 fournit un contre-exemple instructif. L'interdiction de recopier
l'identifiant fictif de l'exemple EST sanctionnee — mais pas parce qu'elle est ecrite dans
le gabarit : parce que `AnswerPolicy` regle 6 rejette tout identifiant absent des extraits.
Le gabarit n'y est pour rien. Quand une clause de gabarit a une consequence, c'est une
regle du Domain qui la lui prete. **[INFERE]** — d'apres le fait qu'aucun code ne lit le
texte de la regle 8 du gabarit 1.2.0.

**Le point ou les deux positions se rencontrent, et qui reste ouvert.** Les deux camps
s'accordent sur le fait mesure : la parole du gabarit et la parole du Domain n'ont pas la
meme force, et le depot ne dit nulle part laquelle vaut quoi. Ils ne s'accordent pas sur ce
qu'il faut en conclure — que le gabarit est une clause faible mais reelle, ou qu'une clause
sans sanction n'est pas une clause. La question se resume peut-etre a celle-ci, qui n'est
pas tranchee ici : un contrat adresse a un executant non deterministe peut-il contenir des
clauses non verifiables, ou est-ce precisement ce qui le distingue d'un contrat ?

---

## 6. Recapitulatif des references

| Fichier | Clauses concernees |
| --- | --- |
| `src/AssistantQR.Domain/Policies/AnswerPolicy.cs` | 1, 2, 3, 4, 5 (tiers 3), 7 (tolerance), 11 |
| `src/AssistantQR.Domain/Answers/Answer.cs` | 1, 2, 7 |
| `src/AssistantQR.Domain/Answers/DraftAnswer.cs` | 5 |
| `src/AssistantQR.Domain/Answers/RefusalReason.cs` | 1, 2, 3, 4, 5 |
| `src/AssistantQR.Domain/Documents/DocumentId.cs` | 6 |
| `src/AssistantQR.Domain/Policies/AccessPolicy.cs` | 4 |
| `src/AssistantQR.Application/Ports/ILanguageModel.cs` | 15, section 4 |
| `src/AssistantQR.Application/Model/LlmRequest.cs` | 12, section 4 |
| `src/AssistantQR.Application/Model/ModelResponseParser.cs` | 5 (tiers 2), 6, 7 |
| `src/AssistantQR.Application/Model/EvidenceFormatter.cs` | 6 |
| `src/AssistantQR.Application/Model/PromptTemplate.cs` | section 5.2 |
| `src/AssistantQR.Application/Configuration/PipelineOptions.cs` | 12 |
| `src/AssistantQR.Application/UseCases/AnswerQuestion/AnswerQuestionUseCase.cs` | 3, 12, 13, 15 |
| `src/AssistantQR.Infrastructure/LanguageModels/OllamaLanguageModel.cs` | 12, 13, 14, 15 |
| `src/AssistantQR.Infrastructure/LanguageModels/ExtractiveLanguageModel.cs` | 12, 15, section 4.2 |
| `src/AssistantQR.Infrastructure/LanguageModels/ReplayLanguageModel.cs` | 12, 15 |
| `src/AssistantQR.Infrastructure/Configuration/LanguageModelOptions.cs` | 13 |
| `src/AssistantQR.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs` | 13, 15 |
| `src/AssistantQR.Infrastructure/Http/JsonHttp.cs` | 13 |
| `src/AssistantQR.Cli/Program.cs` | 13, codes de retour |
| `src/AssistantQR.Cli/Commands/ExitCodes.cs` | codes de retour |
| `src/AssistantQR.Cli/Configuration/CliConfiguration.cs` | 12, 13 (absence de surcharge) |
| `src/AssistantQR.Cli/appsettings.json` | 12, 13 |
| `prompts/answer-with-citations@1.0.0.md` | 5, 6, 8, 9, 10, 11 |
| `prompts/answer-with-citations@1.1.0.md` | 7, 9, 10 |
| `prompts/answer-with-citations@1.2.0.md` | 5, 7, 9, 10, 11 |
| `docs/adr/0006-refus-delibere-et-sortie-vide.md` | 5, 13, section 5.1 |
