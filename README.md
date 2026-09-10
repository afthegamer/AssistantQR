# AssistantQR — Clean Architecture appliquée à une application d'IA

Assistant de questions-réponses sur un corpus documentaire, avec **citations obligatoires**
et **niveaux d'accès**. .NET 8 pour l'application, Python pour le service d'embeddings.

---

## 1. Ce que ce dépôt est, et ce qu'il n'est pas

**Ce qu'il est.** Un support de cours de M1. Il sert à répondre à une question précise :
quand un composant du système est probabiliste, où passent les frontières ? Le code est
écrit pour être projeté, lu à voix haute et discuté — pas pour être déployé.

**Ce qu'il n'est pas.** Un produit. Il n'y a ni authentification, ni journalisation, ni
persistance des conversations, ni mesure de qualité des réponses. Le niveau d'habilitation
est un argument de ligne de commande : le contrôle d'accès est *démontré*, il n'est pas
*sécurisé*. Voir la section 11.

**Refaisable en une quinzaine d'heures**, seul, sans matériel particulier. Le plan de
travail est en section 10.

**Le fil rouge, en trois lignes.** Le modèle de langue est le composant dont on parle le
plus, et c'est un détail remplaçable : on le change entre deux requêtes, rien d'autre ne
bouge. Le modèle d'embeddings est celui dont on ne parle jamais, et c'est un engagement
structurel : on le change, tout le corpus est à réindexer et les réponses se déplacent —
sans la moindre erreur pour prévenir. L'architecture n'a qu'un seul but ici : rendre cette
asymétrie visible plutôt que subie.

---

## 2. Démarrage en 60 secondes

**Aucune clé d'API. Aucun accès réseau. Aucune base vectorielle. Aucun conteneur.**
Le profil par défaut est `offline` : tous les adaptateurs qui parleraient à l'extérieur
sont remplacés par des faux déterministes, dans le processus.

```bash
dotnet test
```

```
Réussi!  - échec : 0, réussite : 176  AssistantQR.Domain.Tests
Réussi!  - échec : 0, réussite : 165  AssistantQR.Application.Tests
Réussi!  - échec : 0, réussite : 179  AssistantQR.Infrastructure.Tests
```

Puis :

```bash
dotnet run --project src/AssistantQR.Cli -- ask "Quels sont les horaires d'ouverture le samedi ?"
```

```
Question
========
Question  : Quels sont les horaires d'ouverture le samedi ?
Demandeur : anonymous (habilitation « public »)

  Index vide : indexation du corpus avant de poursuivre.
  120 morceau(x) issus de 23 document(s), decoupage « paragraph », modele « hashing-fake », en 29
  ms.

Reponse
-------
D'après les documents consultés : La médiathèque des Tilleuls est ouverte du mardi au dimanche et
fermée le lundi. [horaires-ouverture] La nocturne du mercredi, jusqu'à 19h00, est le créneau le plus
fréquenté de la semaine, avec 410 entrées en moyenne. [horaires-ouverture]

Citations
---------
Document            Niveau  Morceau  Extrait
------------------  ------  -------  ---------------------------------------------------------------
horaires-ouverture  public  #0       La médiathèque des Tilleuls est ouverte du mardi au dimanche et
                                     fermée le lundi. Le samedi, l'ouverture est continue de 10h00 à
                                     18h00, sans interruption le midi.

  4 candidat(s) trouves, 4 soumis au modele, en 42 ms. Ajoute --trace pour tout voir.
```

L'index est reconstruit à la volée parce qu'en profil `offline` il vit dans la mémoire du
processus et disparaît avec lui. Sur ce corpus, cela prend une trentaine de millisecondes.

La deuxième commande à taper est celle qui échoue, et c'est la plus instructive :

```bash
assistantqr ask "Quel est le tarif d'entrée à la piscine municipale ?"
```

```
Refus
-----
Motif       : ModelDeclinedToAnswer — le modele a lui-meme declare que les extraits ne suffisaient
              pas
Explication : Le modèle a jugé lui-même que les extraits fournis ne permettaient pas de répondre et
              a préféré s'abstenir plutôt que d'inventer.

  4 candidat(s) trouves, 4 soumis au modele, en 39 ms. Ajoute --trace pour tout voir.
```

Aucun document ne parle de piscine. La recherche remonte quand même quatre morceaux — un
seuil de similarité ne connaît pas le hors-sujet — le modèle est bel et bien appelé, et il
refuse. « Refuser plutôt qu'inventer » n'est donc pas seulement une règle testée dans le
Domain : elle s'observe en ligne de commande, sans rien installer. Voir la section 11.

Le motif mérite d'être lu de près : `ModelDeclinedToAnswer`, **pas**
`ModelProducedEmptyAnswer`. Le modèle a répondu ; sa réponse est « ces extraits ne
permettent pas de répondre ». C'est une **issue métier**, l'obéissance à la consigne que le
gabarit lui impose. `ModelProducedEmptyAnswer` dirait autre chose : le modèle n'a rien
rendu du tout — délai dépassé, sortie tronquée, fournisseur muet — et c'est alors un
**incident technique**. Les deux propositions arrivent au Domain avec un texte vide ; sans
la distinction, une règle métier qui fonctionne se lirait comme une panne dans les
journaux. Voir `docs/adr/0006-refus-delibere-et-sortie-vide.md`.

> **Convention de notation.** Dans la suite, `assistantqr` abrège
> `dotnet run --project src/AssistantQR.Cli --`. Aucune installation n'est nécessaire :
> écrivez la forme longue, ou définissez l'alias de votre shell.

Trois commandes pour prendre la mesure du dépôt :

```bash
assistantqr doctor        # état de l'environnement, configuration effective
assistantqr corpus list   # 23 documents, 11 publics, 8 internes, 4 confidentiels
assistantqr ports         # les 8 frontières et l'adaptateur branché derrière chacune
assistantqr help          # syntaxe complète
```

---

## 3. Le découpage en couches

```
                       sens des DEPENDANCES (compilation)
        <-------------------------------------------------------------

   +-------------+     +-----------------+     +--------------------+     +-------+
   |   Domain    | <-- |   Application   | <-- |   Infrastructure   | <-- |  Cli  |
   +-------------+     +-----------------+     +--------------------+     +-------+
   0 dépendance        -> Domain               -> Application              -> Infra
   pas d'I/O           pas d'I/O               HttpClient, System.Text.Json
   pas d'horloge       pas d'horloge           fichiers, DI, Ollama

   AccessLevel         EmbeddingVector         FileSystemDocumentRepository
   Document            Chunk                   HashingEmbeddingService
   Answer              PromptTemplate          InMemoryVectorIndex
   AccessPolicy        8 ports (interfaces)    OllamaLanguageModel
   AnswerPolicy        3 cas d'usage           JsonFileSnapshotStore

        -------------------------------------------------------------->
                       sens du FLUX D'EXECUTION (appels)
```

Les deux flèches vont en sens inverse, et c'est exactement le sujet.

**La règle d'inversion.** À l'exécution, `AnswerQuestionUseCase` appelle un service
d'embeddings, un index et un modèle de langue — tous trois hors de l'Application. À la
compilation, l'Application ne connaît aucun d'eux. Elle déclare huit **interfaces** dans
`src/AssistantQR.Application/Ports/`, et l'Infrastructure les **implémente**. La flèche de
dépendance est donc retournée : c'est le détail technique qui dépend de la règle métier,
jamais l'inverse.

Le branchement se fait en un seul endroit, la **racine de composition** :
`src/AssistantQR.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`.
Aucune autre classe du dépôt ne fait un `new` sur un adaptateur. La liste complète des
dépendances externes du système tient sur un écran.

**Le test qui garde la porte.** `tests/AssistantQR.Domain.Tests/ArchitectureTests.cs`
échoue si le Domain acquiert une dépendance : il inspecte par réflexion les assemblies
référencées et refuse tout ce qui ne commence pas par `System`, `netstandard` ou
`mscorlib`. Deux autres tests du même fichier vérifient qu'aucun type public du Domain
n'expose `Http`, `Json`, `Vector`, ni `DateTime`/`DateTimeOffset`/`Stopwatch`. Un
quatrième soumet au détecteur un type volontairement fautif : un détecteur qui ne détecte
jamais rien est indiscernable d'un détecteur en panne.

Message d'échec, si quelqu'un ajoute un paquet au Domain :

> Le Domain a acquis une dépendance : X. Le cœur métier doit rester exécutable sans
> réseau, sans base et sans clé d'API : la dépendance ajoutée doit repasser derrière un
> port de l'Application.

`tests/AssistantQR.Application.Tests` ne référence pas `Infrastructure` : ses doublures
sont écrites dans le projet de test lui-même, dossier `Doubles/`. Cette contrainte est
posée dans le `.csproj` — on ne peut donc pas la contourner par distraction.

---

## 4. Les trois règles métier, et où elles vivent exactement

| # | Règle | Fichier | Mécanisme |
|---|---|---|---|
| 1 | Une réponse sans citation n'existe pas | `src/AssistantQR.Domain/Answers/Answer.cs` | Constructeur privé + fabrique `Answer.Create`. La liste de citations vide lève `DomainException`. Le type est **inconstruisible** en violation. |
| 2 | On ne cite jamais un document absent des extraits fournis | `src/AssistantQR.Domain/Policies/AnswerPolicy.cs`, règles 5 et 6 | Chaque identifiant proposé par le modèle est confronté aux fragments réellement transmis. Un identifiant inconnu → `ModelCitedUnknownDocument`. |
| 3 | On ne s'appuie jamais sur un document qu'on n'a pas le droit de lire | `src/AssistantQR.Domain/Policies/AccessPolicy.cs` | `IsReadable`, `Readable`, `Forbidden`. La comparaison de rang est dans `src/AssistantQR.Domain/Access/AccessLevel.cs` (`IsReadableWith`). |

Les trois sont réunies dans une seule fonction, `AnswerPolicy.Decide` — entrée pure,
sortie pure, huit règles évaluées dans un ordre fixe. Aucun appel réseau, aucune clé
d'API, aucune horloge. C'est ce qui permet de tester **exhaustivement le comportement de
l'assistant, refus compris, sans jamais appeler un modèle**.

Le point à faire passer en cours : ce que le modèle de langue rend n'est pas une réponse,
c'est un `DraftAnswer` — une **proposition**
(`src/AssistantQR.Domain/Answers/DraftAnswer.cs`). Elle ne devient une `Answer` qu'après
avoir passé `AnswerPolicy`. Toutes les garanties du système sont **après** le port
`ILanguageModel`, jamais dedans.

La hiérarchie de sortie est fermée : `AnswerOutcome` vaut `Answered` ou `Refused`, pas de
troisième cas. Les **sept** motifs de refus sont énumérés dans
`src/AssistantQR.Domain/Answers/RefusalReason.cs`. Les voici dans l'**ordre d'évaluation**
de `AnswerPolicy.Decide` — la première règle qui échoue détermine la cause :

| # | Motif | Qui a tranché | Nature |
|---|---|---|---|
| 1 | `NoEvidenceInCorpus` | le pipeline | Rien dans le corpus ne se rapproche de la question. Le modèle n'est **pas** appelé. |
| 2 | `NoEvidenceReadableByRequester` | le contrôle d'accès | Des extraits existent, aucun n'est lisible par ce demandeur. Le modèle n'est **pas** appelé. |
| 3 | `ModelDeclinedToAnswer` | le modèle | Il a émis le marqueur de refus que le gabarit lui impose : **issue métier**, la règle « refuser plutôt qu'inventer » qui s'applique. |
| 4 | `ModelProducedEmptyAnswer` | le modèle, en panne | Il n'a rien rendu du tout : délai dépassé, sortie tronquée, fournisseur muet. **Incident technique.** |
| 5 | `ModelProducedNoCitation` | règle métier n° 1 | Un texte, aucune source. Une réponse non sourcée est rejetée. |
| 6 | `ModelCitedUnknownDocument` | règle métier n° 2 | Un identifiant cité ne figure dans aucun extrait fourni : citation inventée. |
| 7 | `ModelCitedForbiddenDocument` | règle métier n° 3 | L'identifiant existe, mais le demandeur n'avait pas le droit de le lire. Le dernier rempart. |

Deux points de lecture. **Le rang dans l'énumération n'est pas l'ordre d'évaluation** :
`ModelDeclinedToAnswer` porte la valeur 6, en fin d'énumération, alors qu'il est évalué en
troisième. Les instantanés JSON déjà enregistrés référencent les causes par nom, mais
décaler les valeurs existantes aurait été un risque gratuit. **Les motifs 3 et 4 sont deux
silences qui ne se ressemblent pas** : le premier est le système qui fonctionne, le second
le système qui tombe en panne. Les confondre reviendrait à compter une règle métier parmi
les incidents. Voir `docs/adr/0006-refus-delibere-et-sortie-vide.md`.

---

## 5. Les huit ports

Huit interfaces dans `src/AssistantQR.Application/Ports/`. Chaque fichier porte un bloc
`<remarks>` qui explique *pourquoi la frontière est à ce niveau plutôt qu'un cran plus
haut ou plus bas*. La commande `assistantqr ports` affiche le même tableau avec la colonne
« adaptateur actif » résolue **dans le conteneur**, donc réellement à jour.

| Port | Ce qu'il abstrait | Adaptateur réel | Adaptateur factice | Pourquoi la frontière est là |
|---|---|---|---|---|
| `IDocumentRepository` | Accès au corpus | `FileSystemDocumentRepository` (Markdown + en-tête YAML) | `InMemoryDocumentRepository` | Le pipeline veut des `Document` déjà valides. Un cran plus bas (flux, chemins) ferait fuir la notion de fichier dans l'Application ; un cran plus haut (des `Chunk`) enterrerait une décision de pertinence dans un lecteur de fichiers. La traduction « interne » → `Internal` est un dialecte du monde extérieur. |
| `IChunkingStrategy` | Découpage d'un document en morceaux | `ParagraphChunkingStrategy`, `FixedSizeChunkingStrategy` | `WholeDocumentChunkingStrategy` | **Le port le plus discutable des huit** : découper un texte ne demande ni réseau ni disque. Il est port par choix pédagogique — c'est le réglage qui illustre le mieux le principe CACE, et en faire une frontière l'oblige à apparaître dans la configuration, dans le montage et dans l'empreinte des instantanés. |
| `IEmbeddingService` | Texte → vecteurs | `HttpEmbeddingService` (service Python) | `HashingEmbeddingService` (FNV-1a, hors ligne, déterministe) | Derrière : un service HTTP, une bibliothèque embarquée, ou trente lignes de hachage. Le pipeline ne fait la différence sur aucune des trois. Deux méthodes et non une, parce que de nombreux modèles exigent un préfixe différent pour une requête et pour un passage. `Model` est exposé : sans lui, l'incohérence index/modèle serait indétectable. |
| `IVectorIndex` | Stockage et recherche des morceaux vectorisés | `HttpVectorIndex` (numpy côté Python) | `InMemoryVectorIndex` (force brute, cosinus) | `SearchAsync` accepte un `SearchFilter` : une notion **métier** entre volontairement dans le contrat d'un composant technique. C'est le dilemme central du cours, posé à découvert. `GetMetadataAsync` existe uniquement pour rendre détectable la panne du scénario B. |
| `ILanguageModel` | Appel au modèle de génération | `OllamaLanguageModel` | `ExtractiveLanguageModel` (extrait, et refuse quand rien ne recoupe la question), `ReplayLanguageModel` | **Le port le plus important, et le plus étroit** : une chaîne entre, une chaîne sort. Pas de conversation, pas d'agents, pas de mémoire, pas d'outils. L'étroitesse est la protection : elle donne le droit de ne jamais faire confiance au modèle. |
| `IPromptCatalog` | Gabarits de prompt versionnés | `FileSystemPromptCatalog` (`nom@version.md`) | `InMemoryPromptCatalog` | Un prompt gouverne le comportement autant que du code, tout en se modifiant sans recompilation. En faire un artefact chargé, nommé, versionné et empreinté permet de le relire sans lire le code et de faire entrer son empreinte dans les instantanés. Interface **synchrone** : le catalogue charge tout au démarrage, un gabarit manquant doit exploser tôt. |
| `IClock` | Lecture de l'heure | `SystemClock` | `FixedClock` | `DateTimeOffset.UtcNow` est une entrée-sortie déguisée. Sans ce port, deux instantanés du même jeu de questions différeraient toujours par leur date et la comparaison serait inutilisable en test. Le plus petit port du projet — la taille d'une frontière ne dit rien de son utilité. |
| `ISnapshotStore` | Conservation des instantanés | `JsonFileSnapshotStore` | `InMemorySnapshotStore` | Le port parle d'`EvaluationSnapshot`, pas de fichiers ni de JSON. Aucun attribut de sérialisation ne remonte dans les types de l'Application. Ce port est l'infrastructure de la seule méthode d'évaluation utilisable ici, pas un utilitaire de journalisation. |

**Huit, et pas une de plus.** Le contre-exemple est instructif : `IRetrievalStrategy`
(pré/post-filtrage) est une interface, elle a deux implémentations, et ce n'est **pas** un
port. Ses deux implémentations vivent dans l'Application et ne parlent qu'à
`IVectorIndex`. Un port se justifie par ce qu'il rend remplaçable **hors du système**, pas
par le fait qu'on puisse en écrire une interface.

Le jeu de questions non plus n'est pas derrière un port : c'est un *argument* de la
commande, lu par `src/AssistantQR.Cli/Commands/QuestionSet.cs`. Ajouter un neuvième port
pour cela ferait entrer dans l'Application une notion qui n'appartient qu'au terminal.

---

## 6. Les quatre tensions, rendues manipulables

### 6.1 Non-déterminisme

Température et graine sont **explicites dans le contrat** :
`LlmRequest(Prompt, Temperature, Seed, MaxTokens, Stop)`. Un port qui les cacherait
rendrait le non-déterminisme invisible. Elles entrent dans l'empreinte de configuration
des instantanés.

Deux faux couvrent les deux besoins : `ExtractiveLanguageModel` ignore délibérément
température et graine — la même requête rend octet pour octet la même réponse, aujourd'hui
et dans six mois — et `ReplayLanguageModel` rejoue des réponses figées dans
`snapshots/replay-llm.json`, appariées par sous-chaîne du prompt.

```bash
assistantqr ask "Y a-t-il une amende si je rends un livre en retard ?" --llm replay
assistantqr ports --llm replay          # ILanguageModel -> ReplayLanguageModel
assistantqr ask "..." --temperature 0.9 --seed 7
```

### 6.2 CACE — *Changing Anything Changes Everything*

La stratégie de découpage est configurable et n'est **pas** une règle métier : le Domain
ignore jusqu'à l'existence des morceaux. Elle change pourtant toutes les réponses.

```bash
assistantqr snapshot record reference
assistantqr snapshot record decoupage-fixe --chunking fixed-600-100
assistantqr snapshot compare reference decoupage-fixe
```

Mesuré sur ce dépôt :

```
Differences de configuration
----------------------------
  - ChunkingStrategyId : paragraph → fixed-600-100

Derive
------
Questions comparees : 12
Reponses modifiees  : 7
Taux de derive      : 58.3 %

Nature             Nombre  Lecture
-----------------  ------  -----------------------------------------------------------------------
RefusalChanged     4       changement de comportement : refus devenu reponse, ou l'inverse
AnswerTextChanged  3       memes sources, meme decision : reformulation, la derive la plus benigne
Identical          5       rien n'a bouge
```

Sept réponses sur douze ont bougé, dont quatre ont changé de décision : un refus est devenu
une réponse, ou l'inverse. Aucune règle métier n'a été touchée, et les deux instantanés
restent également sourcés : l'invariante tient, seul le matériau change. Les quatre
`RefusalChanged` sont l'effet le plus net du découpage : un chunk de 600 caractères ne
recoupe pas la question aux mêmes endroits qu'un paragraphe, donc la frontière entre
« je réponds » et « je refuse » se déplace.
Autres découpages : `--chunking whole-document`, `--chunking fixed-300-50`.

### 6.3 Frontière domaine / modèle : prompts versionnés et empreintes

Les gabarits sont des fichiers `prompts/<nom>@<version>.md` avec en-tête YAML. Le corps est
empreinté (SHA-256 tronqué à 12 caractères) et cette empreinte voyage dans chaque
instantané.

```bash
assistantqr snapshot record avant-1.1.0
assistantqr snapshot record apres-1.1.0 --prompt-version 1.1.0
assistantqr snapshot compare avant-1.1.0 apres-1.1.0
```

```
Differences de configuration
  - PromptVersion : 1.0.0 → 1.1.0
  - PromptFingerprint : c0da209a605a → 7e65cac188ae
```

**Avertissement honnête.** En profil `offline`, cette comparaison donne **0 % de dérive**
(mesuré : 12 entrées identiques sur 12, seule l'empreinte du gabarit change).
C'est normal et instructif : `ExtractiveLanguageModel` ne lit du prompt que deux choses, le
bloc d'extraits et la question. Les instructions — longueur, ton, une citation par
affirmation — lui échappent entièrement. Un modèle qui ignore les consignes du prompt
produit exactement cette signature. Pour observer l'effet réel de 1.1.0 (une citation par affirmation,
aucune phrase d'introduction, trois phrases maximum), il faut un vrai modèle :

```bash
assistantqr snapshot record apres-1.1.0 --prompt-version 1.1.0 \
    --profile local --llm granite4.2:3b --embedding hashing-fake
```

Sans Ollama, cette commande s'arrête proprement, après avoir indexé, sur :

```
Ollama est injoignable sur http://localhost:11434/. Demarre-le (« ollama serve »), verifie que le
modele est installe (« ollama pull granite4.2:3b »), ou bascule AssistantQR:Profile sur « offline ».
Diagnostic complet : assistantqr doctor. Pour travailler sans Ollama : --llm extractive-fake.
```

Le tableau des effets attendus de 1.0.0 → 1.1.0 est dans `prompts/README.md`.

### 6.4 Dérive : instantanés et comparaison

Un système dont un maillon est probabiliste ne se valide pas par assertion exacte. On fige
le comportement du jour sur un jeu de questions fixe (`snapshots/questions.json`,
12 entrées annotées), on change **une** chose, et on mesure.

```bash
assistantqr snapshot record reference
assistantqr snapshot list
assistantqr snapshot compare reference candidat
```

La comparaison affiche **toujours** les différences de configuration à côté du taux de
dérive, parce que les deux listes ne veulent rien dire l'une sans l'autre : 40 % de dérive
assortis d'un changement de version de prompt est un résultat attendu ; les mêmes 40 %
sans aucune différence de configuration sont une alerte.

Quatre natures d'écart, du plus bénin au plus grave : `AnswerTextChanged` (mêmes sources,
reformulation), `CitationsChanged` (la réponse ne s'appuie plus sur le même matériau),
`RefusalChanged` (un refus est devenu une réponse, ou l'inverse), plus les deux cas
d'appariement `MissingInBaseline` / `MissingInCandidate`.

---

## 7. La démonstration centrale : l'asymétrie

L'intuition classe le modèle de langue comme la décision structurante et le modèle
d'embeddings comme un réglage. C'est l'inverse.

### Scénario A — le LLM se remplace

**La ligne de configuration à changer.** Dans `src/AssistantQR.Cli/appsettings.json` :

```jsonc
"LanguageModel": {
  "Model": "extractive-fake",     // <- devient "granite4.2:3b"
  "OllamaUrl": "http://localhost:11434"
},
"Profile": "offline"              // <- devient "local"
```

Les deux réglages sont nécessaires : `extractive-fake` et `replay` sont des noms
**réservés** que la racine de composition reconnaît comme des faux, y compris en profil
`local` (voir `ServiceCollectionExtensions.UsesOllama`). C'est délibéré : cela permet
d'essayer un vrai modèle de langue sans démarrer le service Python, et réciproquement.

**La commande.**

```bash
assistantqr ports                                          # avant
assistantqr ports --profile local --llm granite4.2:3b      # après
```

```
Port                 Adaptateur actif          |  Port                 Adaptateur actif
-------------------  -----------------------   |  -------------------  -----------------------
IDocumentRepository  FileSystemDocumentRepo…    |  IDocumentRepository  FileSystemDocumentRepo…
IChunkingStrategy    ParagraphChunkingStrat…    |  IChunkingStrategy    ParagraphChunkingStrat…
IEmbeddingService    HashingEmbeddingService    |  IEmbeddingService    HashingEmbeddingService
IVectorIndex         InMemoryVectorIndex        |  IVectorIndex         InMemoryVectorIndex
ILanguageModel       ExtractiveLanguageModel    |  ILanguageModel       OllamaLanguageModel   <—
IPromptCatalog       FileSystemPromptCatalog    |  IPromptCatalog       FileSystemPromptCatalog
IClock               SystemClock                |  IClock               SystemClock
ISnapshotStore       JsonFileSnapshotStore      |  ISnapshotStore       JsonFileSnapshotStore
```

**Ce qu'on observe**, en une commande qui n'exige rien d'installé :

```bash
assistantqr demo llm-swap
```

```
  Index partage par les deux montages : 120 morceau(x), modele « hashing-fake »,
  decoupage « paragraph ». Il n'est reconstruit a aucun moment.

Recuperation : identique des deux cotes ?
-----------------------------------------
#   Recuperation  Bruts  Retenus  Question
--  ------------  -----  -------  ----------------------------------------------------------
1   identique     4      4        Quels sont les horaires d'ouverture le samedi ?
2   identique     4      3        Combien de documents puis-je emprunter en même temps et…
…
12  identique     4      3        Que devient le dossier d'un usager qui ne rend pas ses…

Conclusion
----------
Conclusion : passer de « extractive-fake » a « replay » a modifie 6 reponse(s) sur 12 et laisse 12
recuperation(s) sur 12 strictement identiques — le modele de langue a change, le reste du systeme ne
s'en est pas apercu.
```

La colonne « Retenus » varie d'une question à l'autre — 0 quand rien n'est lisible par le
demandeur, 4 quand tout l'est — mais elle est la **même** des deux côtés, ligne par ligne.
C'est cela qu'on lit en séance, pas le nombre de réponses modifiées.

Le second montage reçoit **littéralement la même instance d'index** que le premier :
l'égalité des récupérations n'est pas une coïncidence heureuse, c'est une propriété
vérifiée. La comparaison porte sur les fragments **et** sur leurs scores, à 1e-9 près.

Le test correspondant :
`tests/AssistantQR.Application.Tests/Demonstrations/AsymmetryTests.cs`,
`SwappingLanguageModel_DoesNotChangeRetrieval`.

**Conclusion.** Ici, « le modèle est un détail » est **vrai**. Zéro ligne du Domain, zéro
ligne des cas d'usage, zéro test de politique métier n'a changé. La garantie « une réponse
cite ses sources ou n'existe pas » tient indépendamment du modèle, parce qu'elle n'a jamais
été confiée au modèle.

Corollaire souvent contre-intuitif : **changer de LLM ne répare pas une mauvaise
recherche.** Si les extraits fournis sont hors sujet, le meilleur modèle du monde produira
une réponse hors sujet — ou refusera, ce qui est déjà mieux.

### Scénario B — l'embedding ne se remplace pas

**La réindexation est obligatoire.** Les vecteurs stockés dans l'index vivent dans
l'espace du modèle qui les a produits. Changer de modèle sans reconstruire l'index, c'est
comparer des coordonnées exprimées dans deux repères sans rapport.

**La ligne de configuration à changer.** Dans `src/AssistantQR.Cli/appsettings.json` :

```jsonc
"Embeddings": {
  "Model": "hashing-fake",        // <- devient "hashing-fake-b"
  "Dimension": 1024               // <- ne change pas
}
```

Une ligne, comme au scénario A. La différence tient à ce qu'il faut faire **ensuite** :
réindexer. Personne ne le rappelle, et rien ne le vérifie.

**La commande.** Elle oppose deux faux d'embeddings de **même dimension** :
`hashing-fake` et `hashing-fake-b`. Même algorithme, même déterminisme, seule la graine de
projection change — donc la coordonnée sur laquelle chaque mot atterrit. C'est la situation
de deux vrais modèles concurrents en dimension 1024, jouable sans rien installer.

```bash
assistantqr demo embedding-swap
```

```
Scenario B — changer de modele d'embeddings
===========================================
Avant       : hashing-fake (dimension 1024)
Apres       : hashing-fake-b (dimension 1024)
Questions   : 12
Profil      : offline

Les deux modeles produisent des vecteurs de MEME dimension. Rien ne peut donc planter : l'index
acceptera les vecteurs de l'un comme de l'autre, et c'est precisement ce qui rend la substitution
invisible.

1. Indexation avec le premier modele
  120 morceau(x) de 23 document(s), modele « hashing-fake » en dimension 1024, decoupage
  « paragraph », en 32 ms.
3. Reindexation avec le second modele
  120 morceau(x) de 23 document(s), modele « hashing-fake-b » en dimension 1024, decoupage
  « paragraph », en 12 ms.

5. Cout de la substitution
Reindexation avant      : 32 ms      Dont embeddings (avant) : 10 ms
Reindexation apres      : 12 ms      Dont embeddings (apres) : 5 ms
Taux de derive          : 25.0 %

Differences de configuration
  - EmbeddingModel : hashing-fake → hashing-fake-b

Derive
Questions comparees : 12
Reponses modifiees  : 3
Taux de derive      : 25.0 %

Nature             Nombre  Lecture
-----------------  ------  -----------------------------------------------------------------------
RefusalChanged     1       changement de comportement : refus devenu reponse, ou l'inverse
AnswerTextChanged  2       memes sources, meme decision : reformulation, la derive la plus benigne
Identical          9       rien n'a bouge
```

Une seule ligne de configuration a changé — `AssistantQR:Embeddings:Model` — et
`ConfigurationFingerprint` l'atteste : `EmbeddingModel : hashing-fake → hashing-fake-b`.
Aucune dimension n'a bougé, aucune exception n'a été levée, aucun test n'a cassé.

Un écart, lu de près :

```
3. Combien d'agents tiennent le comptoir d'accueil le samedi matin ? [RefusalChanged]
  Décision modifiée : refus (NoEvidenceReadableByRequester) → réponse.

demo-embedding-avant                             | demo-embedding-apres
-------------------------------------------------+-------------------------------------------------
Refus — NoEvidenceReadableByRequester            | Reponse — sources : horaires-ouverture
scores : (aucun extrait retenu)                  | scores : 0.313
                                                 | D'après les documents consultés : La nocturne du
                                                 | mercredi, jusqu'à 19h00, est le créneau le plus
                                                 | fréquenté de la semaine, avec 410 entrées en
                                                 | moyenne. [horaires-ouverture]
```

C'est le pire cas possible, et il n'a coûté qu'un changement de graine : la question porte
sur les effectifs du comptoir, la réponse porte sur la fréquentation du mercredi. Elle est
en français correct, elle cite un document public réel, elle est parfaitement sourcée — et
elle ne répond pas à la question. Le taux de dérive de 25 % ne dit pas laquelle des deux
versions est la bonne. Il dit seulement que le système a changé d'avis.

**Durée.** Ici, 32 ms pour 120 morceaux — parce que le faux hache du texte. Avec
`qwen3-embedding:0.6b` sur processeur, comptez plusieurs minutes pour le même corpus, et
des heures pour un corpus d'entreprise. C'est le coût réel de la substitution, et il ne se
paie qu'une fois qu'on a compris qu'elle était nécessaire.

**Le point crucial.** La substitution ci-dessus s'est faite **à dimension égale**, et c'est
la seule configuration qui démontre quelque chose. Les deux modèles réels supportés par le
service Python, `qwen3-embedding:0.6b` et `bge-m3`, produisent eux aussi des vecteurs de
**dimension 1024** : le montage hors ligne n'est donc pas une maquette commode, c'est le
même cas de figure. Donc :

- l'index **accepte** les vecteurs : la dimension concorde, le produit scalaire se calcule ;
- le code **compile** : aucune signature n'a changé ;
- les tests d'intégration **passent** : ils vérifient qu'une réponse a été produite, et une
  réponse a été produite ;
- les scores tombent entre 0 et 1 et **ont l'air normaux** ;
- **et le système répond autre chose.**

Rien ne casse. Aucune exception, aucun journal, aucun code de statut ne signale
l'événement. Seule la pertinence s'effondre. C'est la panne silencieuse.

Le test correspondant l'établit sans ambiguïté, sur deux faux de **même dimension** :
`AsymmetryTests.SwappingEmbeddingModel_ChangesRetrieval_WithoutAnyError`.

```csharp
var indexingModel  = new FakeEmbeddingService("modele-d-indexation",     24, salt: 0u);
var queryingModel  = new FakeEmbeddingService("modele-d-interrogation",  24, salt: 2654435761u);
// ... aucune exception, scores dans [-1, 1], et pourtant :
Assert.NotEqual(RetrievedIds(coherent), RetrievedIds(incoherent));
Assert.NotEqual(RetrievedIds(coherent)[0], RetrievedIds(incoherent)[0]);
```

Le document le mieux classé n'est plus le même. La réponse produite sera **sourcée,
plausible, et fausse**.

**Comment on rend la panne visible.** L'index sait avec quel modèle il a été construit :
`IVectorIndex.ResetAsync(model, chunkingStrategyId)` l'enregistre,
`GetMetadataAsync` le restitue. `AnswerQuestionUseCase` compare ce nom à celui du service
d'embeddings courant et produit un `IndexModelWarning` dans la trace. Avec
`StrictIndexModelCheck` (option `--strict`, ou `Pipeline:StrictIndexModelCheck` à `true`),
la même incohérence lève une `InvalidOperationException` au lieu d'avertir.

**Pourquoi le défaut du dépôt est de NE PAS détecter.** Trois raisons :

1. **La panne doit être reproductible pour être enseignable.** Un système qui refuse de
   démarrer ne montre rien. Un système qui répond faux se montre.
2. **C'est le comportement réel des outils du marché.** Le service Python ne refuse
   volontairement jamais une recherche au motif que le vecteur vient d'un autre modèle,
   tant que la dimension correspond (voir `python/embeddings_service/README.md`). La
   plupart des bases vectorielles font exactement pareil.
3. **La détection repose sur une comparaison de chaînes**, donc elle est fragile par
   construction. Un faux qui emprunterait le nom d'un vrai modèle rendrait la substitution
   parfaitement indétectable. C'est pour cette raison que la racine de composition refuse
   de laisser `HashingEmbeddingService` porter le nom configuré : il garde toujours le
   sien.

En production, on met `StrictIndexModelCheck` à `true`. Ici, on le laisse à `false` et on
regarde ce qui se passe.

**L'étape 6 de la démonstration** joue le cas réel : quelqu'un change la configuration et
oublie de réindexer. L'index est celui que `hashing-fake-b` vient de reconstruire ; on
l'interroge avec les vecteurs de `hashing-fake`. À dimension égale, rien ne s'y oppose :

```
6. Et si l'on oublie de reindexer ?
-----------------------------------
Question posee : « Quels sont les horaires d'ouverture le samedi ? » (habilitation public).

Index a jour — questions hashing-fake-b          | Index perime — questions hashing-fake
-------------------------------------------------+-------------------------------------------------
Rendu par l'index :                              | Rendu par l'index :
0.356 horaires-ouverture#0                       | 0.102 budget-acquisitions#0
0.300 horaires-ouverture#4                       | 0.092 desherbage-collections#3
0.298 horaires-ouverture#2                       | 0.085 horaires-ouverture#2
0.260 programme-animations#1                     | 0.078 desherbage-collections#4

Issue : Reponse [horaires-ouverture]             | Issue : Refus (NoEvidenceInCorpus)

AUCUNE EXCEPTION N'A ETE LEVEE. Les deux vecteurs ont la meme dimension, l'index a donc accepte sans
rien remarquer un vecteur produit par un autre modele, calcule ses cosinus et rendu un classement
complet. Les scores sont d'apparence normale ; les coordonnees, elles, ne signifient plus la meme
chose des deux cotes.

  - 1 morceau(x) en commun sur 4 rendus a jour et 4 rendus perimes. Ce qui reste commun n'est pas
    rassurant : c'est ce qui masque la derive au premier coup d'oeil.
  - Le seul signal existant est un champ de la trace, que personne n'est oblige de lire : L'index a
    été construit avec le modèle d'embeddings « hashing-fake-b » alors que la question est encodée
    avec « hashing-fake ». […]
```

Sur cette question précise, l'index périmé rend des scores si bas que le seuil les écarte
tous et que le système refuse : le refus est ici un accident heureux du seuil, pas une
détection. La commande enchaîne ensuite sur la même requête avec `--strict`, où la même
incohérence lève une `InvalidOperationException` au lieu de laisser passer. Les deux mondes
sont montrés côte à côte pour que le défaut permissif se lise comme un choix.

Le cas bruyant reste accessible, et il est le moins intéressant. Avec `--dimensions`, les
deux modèles ne parlent plus le même espace et l'index proteste dès la requête périmée :

```bash
assistantqr demo embedding-swap --dimensions 1024,384
```

```
6. Et si l'on oublie de reindexer ?
L'index a refuse la requete : Comparaison impossible : dimensions différentes (1024 contre 384).
C'est le symptôme d'un index construit avec un autre modèle d'embeddings.

La panne est ici BRUYANTE parce que les dimensions different (1024 contre 384) : tu as demande
« --dimensions ». C'est le cas confortable, et le moins interessant. Relance sans cette option : a
dimension egale, la meme requete passe sans broncher, avec des scores plausibles et des resultats
sans rapport.
```

Avec deux vrais modèles, le parcours est le même — et il demande le service Python :

```bash
assistantqr demo embedding-swap --profile local --models qwen3-embedding:0.6b,bge-m3
```

Les deux font 1024. Rien ne proteste. Sans le service, la commande s'arrête sur :
« *L'index vectoriel est injoignable sur http://localhost:8088/.* »

---

## 8. Le pont C# / Python

### Pourquoi HTTP local et pas un sous-processus

L'alternative naturelle était de lancer un script Python en sous-processus, de lui passer
du JSON sur `stdin` et de lire `stdout`. Trois raisons ont fait pencher la balance.

**1. Des erreurs lisibles.** Une panne arrive avec un code de statut et un corps
structuré :

```json
{"error": {"code": "MODEL_NOT_FOUND",
           "message": "Ollama ne connait pas le modele d'embeddings « bge-m3 ».",
           "hint": "Lance : ollama pull bge-m3"}}
```

Le C# désérialise cet objet et construit une `EmbeddingServiceException` qui dit quoi
taper. En sous-processus, la même panne serait une trace Python noyée dans `stderr`,
mélangée aux avertissements de numpy, à découper à coups d'expressions régulières — et le
code de sortie vaudrait `1` dans tous les cas.

**2. Le modèle est chauffé une seule fois.** Au démarrage, le `lifespan` de `app/main.py`
envoie un embedding à vide : Ollama charge les poids et les garde. Un sous-processus
relancé à chaque appel paierait ce chargement à chaque question — plusieurs secondes, pour
un travail qui en demande quelques centaines de millisecondes.

**3. Le service se teste seul.** `curl http://localhost:8088/health` suffit à savoir si le
problème vient du Python ou du C#, sans SDK .NET installé. La même frontière permet aux
tests de `python/embeddings_service/tests/test_api.py` de tourner **sans Ollama** : le
client est injecté, donc substituable.

Le prix est honnête : un processus de plus à démarrer, un port à surveiller, et le cas
« service pas encore lancé » à gérer. C'est ce que couvrent `docker compose up` et
`GET /health`.

Détail : `python/embeddings_service` **connaît le contrôle d'accès** — `/index/search`
accepte `max_access_level` et pré-filtre. C'est la contrepartie assumée du dilemme de la
section 9a, et c'est visible dans le contrat HTTP lui-même.

### Le lancer en une commande

```bash
docker compose up --build
```

Ou, sans Docker :

```bash
./scripts/start-embeddings.sh          # Linux, macOS, Git Bash
.\scripts\start-embeddings.ps1         # Windows
./scripts/pull-models.sh               # granite4.2:3b, qwen3-embedding:0.6b, bge-m3
```

### Le contrat HTTP, résumé

Base `http://localhost:8088`. JSON UTF-8, champs en `snake_case` (le C# désérialise avec
`JsonNamingPolicy.SnakeCaseLower`). Le niveau d'accès circule **en anglais** :
`public` | `internal` | `confidential`.

| Méthode | Chemin | Entrée | Sortie |
|---|---|---|---|
| GET | `/health` | — | `status`, `embedding_model`, `dimension`, `ollama_reachable`, `index_chunk_count` |
| POST | `/embed` | `texts[]`, `kind` (`query`\|`document`) | `model`, `dimension`, `vectors[][]` |
| POST | `/index/reset` | `model`, `dimension`, `chunking_strategy_id` | `ok` |
| POST | `/index/upsert` | `items[]` (`chunk_id`, `document_id`, `document_title`, `access_level`, `ordinal`, `text`, `vector`) | `count` |
| POST | `/index/search` | `vector`, `top_k`, `max_access_level` (ou `null`) | `results[]` avec `score` |
| GET | `/index/metadata` | — | `embedding_model`, `dimension`, `chunking_strategy_id`, `chunk_count`, `built_at` |

Erreurs : `{"error": {"code", "message", "hint"}}`. Codes : `EMPTY_INPUT`,
`DIMENSION_MISMATCH`, `INVALID_ACCESS_LEVEL`, `INVALID_REQUEST`, `MODEL_NOT_FOUND`,
`INDEX_EMPTY`, `OLLAMA_UNREACHABLE`, `INTERNAL_ERROR`.

Deux points de contrat qui portent tout le cours :

- `max_access_level` fourni → filtrage **avant** le top-k (pré-filtrage) ; `null` → les k
  meilleurs tous niveaux confondus, au client de filtrer (post-filtrage) ;
- `/index/search` **ne refuse jamais** une requête au motif que le vecteur vient d'un autre
  modèle, tant que la dimension correspond. `embedding_model` de `/index/metadata` est le
  modèle qui a **construit** l'index, pas celui que le service utilise aujourd'hui. Les
  deux peuvent différer.

### Le tester au curl seul

```bash
curl -s http://localhost:8088/health

curl -s -X POST http://localhost:8088/embed \
  -H 'Content-Type: application/json' \
  -d '{"texts":["Horaires du samedi"],"kind":"query"}'

curl -s -X POST http://localhost:8088/index/search \
  -H 'Content-Type: application/json' \
  -d '{"vector":[0.1,0.2],"top_k":4,"max_access_level":"internal"}'

curl -s http://localhost:8088/index/metadata
```

Ou tout le contrat d'un coup :

```bash
./scripts/smoke-test.sh                # lecture seule
./scripts/smoke-test.sh --write        # ajoute reset/upsert : DÉTRUIT l'index en place
```

> Sous PowerShell, `curl` est un alias d'`Invoke-WebRequest` et n'accepte pas ces options.
> Utilisez `curl.exe`, ou `scripts/smoke-test.ps1`.

---

## 9. Les deux dilemmes laissés ouverts

Cette section n'a pas de conclusion. Elle est là pour être discutée en séance, et les deux
positions y sont exposées dans leur version la plus forte.

### 9a. Pré-filtrage contre post-filtrage

**Le fait technique d'abord.** En recherche vectorielle top-k, filtrer avant et filtrer
après ne donnent pas le même résultat, et **les deux ensembles de candidats ne sont pas
sous-ensembles l'un de l'autre**. Ce n'est pas une subtilité : c'est la conséquence
mécanique du fait que `k` est une contrainte de cardinalité appliquée à un classement.
Filtrer avant, c'est classer un autre ensemble ; les places libérées par les documents
interdits sont reprises par des documents qui n'auraient jamais atteint le top-k.

La commande qui montre la divergence :

```bash
assistantqr demo access-filter \
  "Que devient le dossier d'un usager qui ne rend pas ses documents malgré les relances ?" \
  --user agent-accueil --clearance internal
```

```
CAS UNIQUE, topK = 4 — divergence des extraits livres
-----------------------------------------------------
PRE-filtrage (l'index filtre)                    | POST-filtrage (le Domain filtre)
-------------------------------------------------+-------------------------------------------------
Rendu par l'index :                              | Rendu par l'index :
0.483 gestion-retards-interne#0 (internal)       | 0.483 gestion-retards-interne#0 (internal)
0.320 retards-amendes#0 (public)                 | 0.320 retards-amendes#0 (public)
0.269 budget-acquisitions#4 (internal)           | 0.297 contentieux-usagers#0 (confidential)
0.257 inventaire-annuel#1 (internal)             | 0.269 budget-acquisitions#4 (internal)

Soumis au modele :                               | Soumis au modele :
0.483 gestion-retards-interne#0 (internal)       | 0.483 gestion-retards-interne#0 (internal)
0.320 retards-amendes#0 (public)                 | 0.320 retards-amendes#0 (public)
0.269 budget-acquisitions#4 (internal)           | 0.269 budget-acquisitions#4 (internal)
0.257 inventaire-annuel#1 (internal)             |

Ecarte par le seuil de score : (aucun)           | Ecarte par le seuil de score : (aucun)

Ecarte par le controle d'acces : (aucun)         | Ecarte par le controle d'acces :
                                                 | 0.297 contentieux-usagers#0 (confidential)

  - Pre-filtrage : 4 candidat(s) rendus, tous deja lisibles ; le topK n'a ete gaspille pour
    personne, 4 extrait(s) atteignent le modele.
  - Post-filtrage : 4 candidat(s) rendus dont 1 retire(s) par le controle d'acces apres coup ; 3
    extrait(s) atteignent le modele.
```

Même question, même corpus, même utilisateur, même `topK`, même seuil. Le pré-filtrage a
fait remonter `inventaire-annuel#1`, que le post-filtrage n'a **jamais eu l'occasion de
considérer** : ce fragment était trop bas dans le classement général pour entrer dans le
top-k. Le post-filtrage a livré trois extraits au modèle, le pré-filtrage quatre :
`contentieux-usagers#0` a consommé une place, puis a été retiré, et **personne n'a repris
la place**. C'est la forme forte du dilemme — celle qui se lit dans le compte des extraits.

Il en existe une seconde, plus trompeuse : les deux modes livrent le même ensemble
d'extraits, et la divergence n'existe que dans le classement intermédiaire. La commande la
cherche à un `topK` plus petit et affiche alors deux cas. Sur cette question, la forme forte
apparaît dès le `topK` configuré, et la commande le dit :

> Inutile de reduire le topK pour l'exhiber : des 4, les deux modes ne livrent deja pas le
> meme ensemble d'extraits. […] Augmente le topK avec --topk pour le retrouver.

Pour voir les deux formes dans une seule exécution, il faut un `topK` configuré où les deux
modes convergent et un `topK` plus petit où ils divergent :

```bash
ASSISTANTQR_PIPELINE__TOPK=10 assistantqr demo access-filter \
  "Y a-t-il une amende si je rends un livre en retard ?" \
  --user agent-accueil --clearance internal --min-score 0.15
```

```
CAS 1, topK = 9 — divergence des extraits livres
  - Post-filtrage : 9 candidat(s) rendus dont 1 retire(s) par le controle d'acces apres coup ; 8
    extrait(s) atteignent le modele.
  grille-remuneration#5 a consomme une place du topK avant d'en etre retire, et cette place est
  perdue : personne ne la reprend, le modele travaille avec moins de matiere.

CAS 2, topK = 10 — divergence invisible
  Les deux modes livrent le meme ensemble d'extraits au modele : le dilemme ne se lit que dans la
  trace. C'est le cas le plus trompeur.
  En post-filtrage, grille-remuneration#5 occupait une place du topK avant d'en etre retire par le
  controle d'acces. En pre-filtrage, cette place est revenue a gestion-retards-interne#1, que le
  seuil de score a ensuite ecarte.
```

Neuf extraits livrés d'un côté, huit de l'autre au `topK` 9 ; neuf des deux côtés au
`topK` 10, pour deux chemins différents. Le second cas est celui qui doit inquiéter : la
réponse est identique, la divergence est réelle, et seule la trace la montre.

Le test qui l'atteste, avec des scores choisis pour que l'arbitrage se voie :
`tests/AssistantQR.Application.Tests/Retrieval/RetrievalStrategyTests.cs`,
`RetrieveAsync_SameIndexSameRequester_PreAndPostFilteringDiverge`. Il asserte explicitement
la **non-inclusion mutuelle** des candidats :

```csharp
Assert.False(postCandidates.IsSubsetOf(preCandidates));
Assert.False(preCandidates.IsSubsetOf(postCandidates));
```

**La position pré-filtrage, dans sa forme la plus forte.**
Le contrôle d'accès n'est pas un post-traitement cosmétique, c'est une propriété de
l'ensemble sur lequel on travaille. Un système sûr *par construction* est un système où le
document interdit **n'entre jamais** dans le pipeline : ni dans le classement, ni dans le
prompt, ni dans un journal, ni dans un cache, ni dans une trace de débogage envoyée par
courriel à un collègue. Le post-filtrage, lui, fait transiter par la mémoire de
l'application des documents que le demandeur n'a pas le droit de voir, et fait reposer la
sécurité sur une seule ligne de code exécutée correctement au bon moment. La défense en
profondeur n'est pas un argument contre le pré-filtrage : elle vaut dans les deux modes.
De surcroît, le pré-filtrage rend au demandeur un top-k entièrement utile. Refuser de
répondre à un agent alors que le corpus contenait la réponse, parce que trois documents
confidentiels ont mangé le classement, n'est pas une position architecturalement pure :
c'est une régression fonctionnelle payée au nom d'un principe.

**La position post-filtrage, dans sa forme la plus forte.**
Une règle métier écrite à deux endroits est une règle qui dérivera. Le pré-filtrage pousse
le contrôle d'accès dans le contrat de l'index — donc dans l'infrastructure, donc hors de
portée des tests du Domain, donc dans un composant que l'on remplacera un jour par un
autre fournisseur avec une autre sémantique de filtre. Le jour où l'adaptateur traduit mal
`internal` vers le dialecte de la base, ou ignore silencieusement un filtre qu'il ne sait
pas exprimer, la faille est ouverte et aucun test du cœur ne la voit. Le post-filtrage
garde la règle en un seul endroit, `AccessPolicy`, testable exhaustivement, sans réseau, en
quelques millisecondes. Le coût — un top-k parfois appauvri — est un coût de **pertinence**,
pas un coût de **sûreté** ; et un système qui répond moins bien est infiniment préférable à
un système qui répond ce qu'il ne devrait pas.

**Ce que chaque camp doit assumer.**
Le pré-filtrage doit expliquer comment on teste une règle métier qui vit dans un adaptateur
HTTP, et ce qui se passe le jour où l'index change de fournisseur. Le post-filtrage doit
assumer que des documents interdits traversent réellement la mémoire de l'application, et
que `AnswerPolicy` devient le **dernier rempart** : si le modèle cite un document qu'il a
vu mais que le demandeur ne pouvait pas lire, c'est `ModelCitedForbiddenDocument` qui
sauve la situation — un refus, pas une prévention.

Le dépôt implémente **les deux**, choisit `post` par défaut, et laisse le mode dans
l'empreinte de configuration des instantanés. Voir `docs/adr/0003-filtrage-acces.md`.
Il ne tranche pas.

Un dernier fait, à lire honnêtement : sur l'ensemble **final**, le pré-filtrage ne peut
jamais rendre un résultat strictement pire, parce que « les k meilleurs parmi les
lisibles » contient nécessairement « les lisibles parmi les k meilleurs ». Le pré-filtrage
ne se paie donc pas en pertinence. Il se paie en architecture. Ceux qui défendent le
post-filtrage doivent le savoir : leur argument n'est pas « c'est mieux », c'est « c'est
plus sûr à long terme ».

### 9b. La frontière du prompt

Le corps de `prompts/answer-with-citations@1.0.0.md` contient cette phrase :

> Cite tes sources en ligne, entre crochets, en reprenant l'identifiant EXACT tel qu'il
> apparaît en tête de l'extrait utilisé.

C'est mot pour mot la règle métier n° 1, celle que `Answer` rend impossible à violer. La
même règle est donc écrite à deux endroits, dans deux langages, dans deux couches. Et le
parseur de la réponse du modèle — `ModelResponseParser`, qui extrait les identifiants avec
l'expression régulière `\[([A-Za-z0-9][A-Za-z0-9_\-]{0,127})\]` — est le **pendant exact**
du gabarit : les deux encodent le même format, et une modification de l'un sans l'autre
casse le contrat en silence.

Où passe la frontière ?

**Argument pour l'infrastructure.**
Le domaine ne doit connaître aucun mécanisme d'exécution. Or ce texte est saturé de
mécanisme : il parle de crochets, de marqueur de refus, de plafond de longueur, de
formatage ligne à ligne. Il est calibré pour *ce* modèle-ci — un autre demanderait une
autre formulation pour obtenir le même résultat métier. Rien de tout cela n'est stable. La
preuve : la règle métier tient déjà entièrement dans `AnswerPolicy` et `Answer`, qui n'ont
jamais lu un prompt de leur vie. Le gabarit n'est pas la règle, c'est une **tentative de
persuasion** d'un composant externe pour qu'il produise une entrée acceptable. Une
tentative faillible, spécifique à un fournisseur, remplaçable : c'est la définition même
d'un détail d'infrastructure.

**Argument pour le domaine.**
La règle « une réponse cite ses sources » ne vient pas d'un choix technique. Elle vient de
la médiathèque, qui refuse d'engager sa parole sans référence vérifiable. Elle survivrait à
un remplacement complet de la pile technique — et ce qui survit au changement de
technologie est, par définition, du domaine. Le gabarit n'est pas « du texte pour un
LLM » : c'est la formulation de l'obligation métier à destination d'un exécutant
particulier, non déterministe mais exécutant. Le placer en infrastructure revient à laisser
une règle métier être modifiée par quelqu'un qui croit n'ajuster qu'un paramètre de
performance. **Voir une règle métier migrer dans un fichier markdown non testé devrait
inquiéter.** Le fait que le modèle n'obéisse qu'imparfaitement ne change rien : un règlement
intérieur n'est pas moins du domaine parce que les agents s'en écartent parfois.

**Où le dépôt les a mis, et pourquoi ce choix est discutable.**

| Artefact | Couche | Fichier |
|---|---|---|
| Corps du gabarit | Infrastructure (fichier), derrière `IPromptCatalog` | `prompts/answer-with-citations@1.0.0.md`, chargé par `FileSystemPromptCatalog` |
| Type `PromptTemplate` et son empreinte | Application | `src/AssistantQR.Application/Model/PromptTemplate.cs` |
| Parseur de la réponse | **Application** | `src/AssistantQR.Application/Model/ModelResponseParser.cs` |
| Vérification de la règle | Domain | `src/AssistantQR.Domain/Policies/AnswerPolicy.cs` |

Le dépôt tranche par la pratique : le gabarit est traité comme de l'infrastructure, mais
**surveillé comme du domaine** — son empreinte SHA-256 est enregistrée dans chaque
instantané, donc un changement de virgule est attribuable.

Ce compromis est discutable sur trois points, qu'il faut nommer :

1. **Le parseur et le gabarit sont dans deux couches différentes.** Le parseur est en
   Application, le gabarit en Infrastructure, alors qu'ils encodent le même format et
   doivent bouger ensemble. C'est incohérent, et le commentaire du parseur l'admet.
2. **L'empreinte détecte le changement, pas la régression.** Elle dit qu'on a modifié le
   texte. Elle ne dit pas que la modification a supprimé l'obligation de citer. Rien dans
   le dépôt ne teste que le corps d'un gabarit contient encore l'instruction de citation.
3. **La duplication est réelle et non gérée.** Les deux formulations de la règle peuvent
   dériver l'une de l'autre sans que rien ne le signale. Le seul filet est que
   `AnswerPolicy` refusera — mais un refus systématique après une modification malheureuse
   du prompt sera d'abord attribué au modèle.

Voir `docs/adr/0004-prompts-versionnes.md` et la section correspondante de
`prompts/README.md`. Il n'y a pas de conclusion ici non plus.

---

## 10. Comment refaire ce dépôt soi-même

Plan de travail en dix étapes, dans l'ordre des dépendances. Les durées sont des ordres de
grandeur pour quelqu'un qui connaît C# et découvre le sujet.

| # | Étape | Ce qu'on écrit | Durée |
|---|---|---|---|
| 1 | **Squelette** | `AssistantQR.sln`, `global.json`, `Directory.Build.props` (net8.0, nullable, `TreatWarningsAsErrors`), les 7 `.csproj` et leurs références. C'est ici qu'on **interdit** à `Application.Tests` de référencer `Infrastructure`. | 1 h |
| 2 | **Domain : les types** | `AccessLevel`, `UserId`, `Requester`, `DocumentId`, `Document`, `Question`, `EvidenceFragment`, `Citation`, `Answer`, `DraftAnswer`, `AnswerOutcome`, `RefusalReason`, `TextExcerpt`. Constructeurs privés, fabriques validantes. | 2 h |
| 3 | **Domain : les politiques + tests** | `AccessPolicy`, `AnswerPolicy` (8 règles, ordre fixe) et leurs tests. `ArchitectureTests` en dernier — c'est le détecteur de fumée. Écrire les tests **avant** de passer à l'étape 4 : c'est la seule partie du système dont la justesse est démontrable. | 2 h |
| 4 | **Application : modèles et ports** | `EmbeddingVector`, `Chunk`, `ScoredFragment`, `SearchFilter`, `IndexMetadata`, `LlmRequest`, `PromptTemplate`, `PipelineOptions`. Les 8 interfaces de `Ports/`, avec leur bloc `<remarks>` — c'est le moment de justifier chaque frontière par écrit, pas après. | 2 h |
| 5 | **Application : cas d'usage** | `AnswerQuestionUseCase`, `IndexCorpusUseCase`, les stratégies `Pre`/`PostFilterRetrievalStrategy`, `ModelResponseParser`, `EvidenceFormatter`. Tests avec des doublures écrites à la main dans `Doubles/`. | 2 h 30 |
| 6 | **Infrastructure — lot A** | `FileSystemDocumentRepository` + `FrontMatterParser`, les trois stratégies de découpage, `FileSystemPromptCatalog`, `SystemClock`/`FixedClock`, `JsonFileSnapshotStore`, plus les doubles en mémoire. | 2 h |
| 7 | **Infrastructure — lot B** | `HashingEmbeddingService` (FNV-1a + normalisation L2), `InMemoryVectorIndex`, `ExtractiveLanguageModel`, `ReplayLanguageModel`, puis les adaptateurs HTTP et leurs exceptions. Les faux d'abord : ce sont eux qui rendent le dépôt utilisable. | 1 h 30 |
| 8 | **Racine de composition + CLI** | `AssistantOptions`, `PathResolver`, `ServiceCollectionExtensions`, le dispatcher, `ask`, `index`, `corpus`, `doctor`, `ports`, `snapshot`. Le rendu console coûte plus cher qu'on ne croit. | 2 h |
| 9 | **Contenu** | 23 documents de corpus avec vocabulaire volontairement recouvrant entre niveaux, 3 gabarits de prompt, `questions.json` (12 entrées annotées), `replay-llm.json`. **Le recouvrement lexical est la condition des démonstrations** : sans lui, le dilemme du filtrage ne se déclenche jamais. | 1 h 30 |
| 10 | **Démonstrations et documentation** | `demo llm-swap`, `demo embedding-swap`, `demo access-filter`, `AsymmetryTests`, README et ADR. | 1 h 30 |
| — | *(facultatif)* **Service Python** | FastAPI, client Ollama, index numpy, gestion d'erreurs typées, tests sans Ollama, Dockerfile. | 2 h |

**Total : environ 16 heures**, dont 14 sans le service Python. Le chemin critique est
1 → 2 → 3 → 4 → 5 : après l'étape 5, le système est démontrable avec des doublures et tout
le reste est du confort.

Deux conseils de méthode :

- **N'écrivez pas l'Infrastructure avant que les tests de l'Application passent.** Si vous
  avez besoin d'un adaptateur réel pour tester un cas d'usage, la frontière est mal placée.
- **Écrivez le faux avant le vrai.** `HashingEmbeddingService` avant `HttpEmbeddingService`,
  `ExtractiveLanguageModel` avant `OllamaLanguageModel`. Le faux force à définir le
  contrat minimal ; le vrai le remplit ensuite.

---

## 11. Limites assumées et pièges connus

**Le faux d'embeddings n'est pas un modèle.** `HashingEmbeddingService` projette mots et
bigrammes sur des coordonnées par hachage FNV-1a, puis normalise. Il produit une similarité
**lexicale**, pas sémantique : « tarif » et « prix » n'ont aucun rapport pour lui. Les
scores tournent autour de 0,2–0,5 là où un vrai modèle donnerait 0,6–0,8. Il est là pour
que les tests hors ligne soient *significatifs*, pas pour être bon.

**La dimension par défaut est 1024, et non 256.** À 256 coordonnées pour ~120 morceaux, les
collisions de hachage suffisent à rendre le classement bruyant : une question sur les
amendes de retard remontait des documents sur le désherbage. Le commentaire de
`appsettings.json` le documente. C'est un réglage de recherche, pas une règle métier.

**Le faux de génération n'obéit au prompt que sur un point.** `ExtractiveLanguageModel`
relit le bloc d'extraits, écarte ceux qui ne recoupent pas la question, garde les deux
premiers qui restent et en recopie la première phrase. S'il ne reste rien, il émet le
marqueur de refus — c'est la seule consigne du gabarit qu'il applique. Le critère est un
**recoupement lexical** : deux mots significatifs de la question retrouvés au même endroit
d'un bloc, dans l'en-tête ou dans le texte, jamais en additionnant les deux. Ce n'est pas
une compréhension. Ce refus-là remonte en `ModelDeclinedToAnswer` : la doublure **obéit** au
gabarit, elle ne tombe pas en panne. Il refuse une question posée avec des synonymes, et il accepte un
extrait qui partage le vocabulaire sans répondre — les entrées 8 et 9 de `questions.json`
le montrent : le montant demandé est confidentiel, le faux cite un document interne qui
parle du même contrat sans donner le chiffre, et produit une réponse sourcée et à côté.
Toutes les instructions restantes du gabarit — longueur, ton, une citation par affirmation —
lui échappent : la comparaison de versions de prompt donne donc **0 % de dérive** en profil
`offline` (section 6.3). Il faut Ollama pour observer cet effet. Voir
`docs/adr/0005-doublure-modele-de-langue.md`.

**En `offline`, l'index vit dans la mémoire du processus.** Il disparaît à chaque sortie du
programme, et chaque commande le reconstruit (≈ 30 ms). `assistantqr index` n'a donc de
sens qu'en profil `local`. La commande le dit elle-même.

**`--profile local` seul ne suffit pas à basculer.** `extractive-fake`, `replay` et
`hashing-fake` sont des noms **réservés** qui restent des faux dans les deux profils. Pour
un vrai montage : `--profile local --llm granite4.2:3b --embedding bge-m3 --dimension 1024`.
Vérifiez toujours avec `assistantqr ports` — c'est fait pour.

**Le taux de dérive mesure le changement, pas la qualité.** Il n'y a **pas de vérité
terrain** dans ce dépôt : aucune réponse attendue, aucune mesure d'exactitude. Une dérive
de 0 % peut signifier « rien n'a bougé » aussi bien que « le jeu de questions ne discrimine
rien ». Douze questions ne sont pas un jeu d'évaluation, c'est un jeu de démonstration.

**Le contrôle d'accès est démontré, pas sécurisé.** L'habilitation est un argument de
ligne de commande : `--clearance confidential` suffit à tout lire. Il n'y a ni
authentification, ni session, ni audit. Dans un vrai système, `Requester` viendrait d'un
jeton vérifié, et le pré-filtrage se discuterait autrement.

**Le magasin d'instantanés liste tous les `.json` du dossier**, y compris `questions.json`
et `replay-llm.json`, qui n'en sont pas. `snapshot list` le signale au lieu d'échouer :
« *n fichier(s) JSON du dossier ne sont pas des instantanés* ». C'est un compromis assumé
entre simplicité et propreté.

**`IChunkingStrategy` est un port par choix pédagogique**, pas par nécessité technique.
Découper un texte ne demande ni réseau ni disque. On aurait pu écrire les trois stratégies
dans l'Application, comme `IRetrievalStrategy`. Le fichier le dit lui-même. C'est un point
légitime de désaccord en séance.

**Le pipeline ne vérifie pas la pertinence de ce qui reste après filtrage**, seulement
l'habilitation. Les entrées 10 et 11 de `questions.json` le montrent : aucun document ne
parle de piscine ni de prêt de vélos, quatre fragments franchissent quand même le seuil, et
le modèle est appelé. Ici il refuse, et le motif dit **lequel** des deux silences on
observe : `ModelDeclinedToAnswer`, le modèle a parlé pour dire qu'il ne pouvait pas
répondre — et non `ModelProducedEmptyAnswer`, qui signalerait un modèle muet. Un autre
modèle répondrait, correctement cité et totalement à côté. La garantie ne vient jamais du fait que
les extraits soient pertinents — rien ne le vérifie — mais du fait qu'une réponse sans
citation vérifiable ne peut pas exister.

**Pas de re-classement, pas de BM25, pas d'hybride, pas d'expansion de requête.** Une seule
recherche vectorielle, un seuil, un top-k. Toute la littérature RAG des trois dernières
années est absente, volontairement : elle ajouterait du bruit à un cours qui porte sur les
frontières, pas sur la pertinence.

**Sous Windows**, la console doit être en UTF-8 pour que les accents s'affichent.
`Program.UseUtf8()` s'en charge quand c'est possible, et n'interrompt pas le programme
quand la sortie est redirigée vers un flux qui impose son encodage.

---

## Où aller ensuite

| Document | Contenu |
|---|---|
| `docs/PLAN-DE-COURS.md` | Découpage en séances : fichiers à projeter, commandes à démontrer en direct. |
| `docs/adr/0001-pont-http-python.md` | Pourquoi HTTP local plutôt qu'un sous-processus. |
| `docs/adr/0002-vecteur-hors-domaine.md` | Pourquoi `EmbeddingVector` est en Application et non en Domain. |
| `docs/adr/0003-filtrage-acces.md` | Pré ou post-filtrage : les deux, configurable. |
| `docs/adr/0004-prompts-versionnes.md` | Le prompt comme artefact versionné et empreinté. |
| `docs/adr/0005-doublure-modele-de-langue.md` | Pourquoi la doublure de modèle de langue refuse, et ce qu'elle ne peut pas simuler. |
| `docs/adr/0006-refus-delibere-et-sortie-vide.md` | Pourquoi un refus déclaré par le modèle n'est pas une sortie vide, et ce qu'un seul motif ferait perdre. |
| `corpus/README.md` | Format des documents, convention d'identifiant, contrainte de recouvrement lexical. |
| `prompts/README.md` | Convention de nommage, effet attendu de 1.0.0 → 1.1.0, la question ouverte de la frontière. |
| `python/embeddings_service/README.md` | Contrat HTTP complet, codes d'erreur, la panne silencieuse en clair. |
