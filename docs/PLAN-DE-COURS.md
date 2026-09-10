# Plan de cours — Clean Architecture appliquée à une application d'IA

Public : M1 informatique. Format : six séances de trois heures, cours et manipulation
mêlés. Le dépôt est projeté en permanence ; chaque séance se termine sur une commande qui
tourne devant les étudiants.

**Thèse du cours, à énoncer en séance 1 et à vérifier en séance 5 :** dans un système à
composant probabiliste, les frontières ne se placent pas où l'intuition les met. Le
composant dont on parle le plus — le modèle de langue — est un détail remplaçable. Celui
dont on ne parle jamais — le modèle d'embeddings — est un engagement structurel.

---

## Avant la première séance

À faire vérifier par les étudiants, une semaine avant :

```bash
dotnet --version          # 8.0 ou supérieur
git clone <dépôt> && cd FYC
dotnet test               # 520 tests, aucun échec, aucun réseau
dotnet run --project src/AssistantQR.Cli -- doctor
```

Rien d'autre n'est requis pour les séances 1 à 4. Ollama et le service Python ne servent
qu'en séance 5, et une seule fois — la version hors ligne de la démonstration suffit si
l'installation résiste.

Dans tout ce document, `assistantqr` abrège `dotnet run --project src/AssistantQR.Cli --`.

---

## Séance 1 — Le problème, et pourquoi l'architecture habituelle ne suffit pas

**Objectif.** Faire admettre qu'un système dont un maillon ne peut pas être testé par
assertion exacte pose un problème d'architecture, pas un problème de qualité de modèle.

### Fichiers à projeter

| Fichier | Ce qu'on montre |
|---|---|
| `README.md`, sections 1 et 2 | Ce que le dépôt est et n'est pas. Le fil rouge en trois lignes. |
| `corpus/horaires-ouverture.md`, `corpus/grille-remuneration.md` | Le corpus : en-tête YAML, niveaux d'accès, faits chiffrés. |
| `corpus/README.md`, section « contrainte de recouvrement » | Pourquoi le vocabulaire se recoupe entre niveaux. Sans cela, aucune démonstration ne se déclenche. |
| `snapshots/questions.json` | Les douze questions et leurs notes. Lire à voix haute les entrées 5/6 (même question, deux habilitations) et 12 (l'entrée pivot). |
| `Directory.Build.props`, les sept `.csproj` | Les dépendances entre projets. `Domain.csproj` n'a aucune `ProjectReference`. |

### Commandes à démontrer en direct

```bash
dotnet test
assistantqr ask "Quels sont les horaires d'ouverture le samedi ?"
assistantqr ask "Combien d'agents tiennent le comptoir d'accueil le samedi matin ?"
assistantqr ask "Combien d'agents tiennent le comptoir d'accueil le samedi matin ?" \
    --user agent-accueil --clearance internal
assistantqr ask "Quel est le tarif d'entrée à la piscine municipale ?"
```

Les deux commandes du milieu sont **la même question** : seule l'habilitation change. La
première des deux refuse avec `NoEvidenceReadableByRequester` — le pipeline n'a rien de
lisible à soumettre, le modèle n'est même pas appelé. La seconde répond en citant
`procedure-accueil`.

La dernière commande est un refus d'une autre nature, et c'est elle qu'il faut commenter :
aucun document ne parle de piscine, mais quatre morceaux franchissent quand même le seuil de
similarité, le modèle **est** appelé, et il refuse — `ModelDeclinedToAnswer`. Deux motifs
de refus, deux endroits du système, une seule sortie lisible. Faire nommer la différence par
la classe avant de la donner.

Insister sur le nom du motif : `ModelDeclinedToAnswer` dit que le modèle a **répondu**, et
que sa réponse est « ces extraits ne suffisent pas ». Il obéit au gabarit. Le motif voisin,
`ModelProducedEmptyAnswer`, dirait tout autre chose : le modèle n'a rien rendu — panne,
délai, fournisseur muet. Question à poser avant de montrer la réponse : *à quoi
ressemblent, à la sortie du modèle, une règle métier qui fonctionne et une panne ?* Les
deux rendent un texte vide. C'est le sujet de `docs/adr/0006-refus-delibere-et-sortie-vide.md`,
et c'est un bon amorçage de la séance 2.

### Discussion à ouvrir

- Comment testeriez-vous « la réponse est correcte » ? Réponse attendue : on ne peut pas.
  D'où le reste du cours.
- Qu'est-ce qui, dans ce système, est déterministe et vérifiable ? Faire lister.
- Où mettriez-vous la règle « une réponse doit citer ses sources » ? Noter les réponses au
  tableau, y revenir en séance 2.

### Exercice de fin de séance

Ajouter un document au corpus, poser une question dessus, observer. Puis retirer la clé
`niveau` de son en-tête et lire le message d'erreur de `FrontMatterParser`.

---

## Séance 2 — Le Domain : rendre l'invariante inconstruisible

**Objectif.** Montrer qu'une règle métier portée par un type est plus solide que la même
règle portée par une validation, un test d'intégration ou une revue de code.

### Fichiers à projeter

| Fichier | Ce qu'on montre |
|---|---|
| `src/AssistantQR.Domain/Access/AccessLevel.cs` | `readonly record struct`, `default` = `Public` (défaut sûr), `IsReadableWith`. Pourquoi la traduction du français n'est **pas** ici. |
| `src/AssistantQR.Domain/Documents/Document.cs` | Entité : égalité par `Id`. Contraste avec les value objects du dossier voisin. |
| `src/AssistantQR.Domain/Answers/Answer.cs` | **Le fichier central.** Constructeur privé, fabrique `Create`, `DomainException` si la liste de citations est vide. Le type est inconstruisible en violation. |
| `src/AssistantQR.Domain/Answers/DraftAnswer.cs` | Ce que le modèle *prétend* avoir produit. Une proposition, pas une réponse. Le drapeau `Declined` : deux textes vides qui ne veulent pas dire la même chose. |
| `src/AssistantQR.Domain/Answers/AnswerOutcome.cs` | Hiérarchie fermée : on répond ou on refuse, pas de troisième cas. |
| `src/AssistantQR.Domain/Policies/AnswerPolicy.cs` | Les huit règles, dans l'ordre. Entrée pure, sortie pure. **Lire l'en-tête du fichier à voix haute**, puis le commentaire de la règle 3 : pourquoi la distinction refus déclaré / sortie vide vit ici et non dans l'adaptateur. |
| `src/AssistantQR.Domain/Policies/AccessPolicy.cs` | La règle n° 3, en un seul endroit. « Un pré-filtrage est une optimisation ; la politique reste la vérité. » |
| `tests/AssistantQR.Domain.Tests/AnswerPolicyTests.cs` | Les sept motifs de refus, testés exhaustivement, en quelques millisecondes. |
| `tests/AssistantQR.Domain.Tests/ArchitectureTests.cs` | Le détecteur de fumée, et son contrôle négatif. |

### Commandes à démontrer en direct

```bash
dotnet test tests/AssistantQR.Domain.Tests    # 176 tests, ~140 ms
```

**Manipulation à faire devant la classe.** Ajouter une `ProjectReference` vers
`Newtonsoft.Json` ou vers `Application` dans `AssistantQR.Domain.csproj`, relancer, lire le
message d'échec :

> Le Domain a acquis une dépendance : X. […] Le cœur métier doit rester exécutable sans
> réseau, sans base et sans clé d'API.

Puis annuler la modification. C'est la démonstration la plus efficace de la séance : un
diagramme de couches ne tient pas six mois, un test si.

### Discussion à ouvrir

- Pourquoi `EvidenceFragment` ne porte-t-il pas de score de similarité ?
  (Renvoyer à `docs/adr/0002-vecteur-hors-domaine.md`.)
- Pourquoi `DateTimeOffset.UtcNow` est-il interdit ici ? Réponse : c'est une entrée-sortie
  déguisée. Amorce du port `IClock` en séance 3.
- Reprendre les réponses notées en séance 1 sur « où mettre la règle de citation ».

### Exercice

Écrire le test qui échoue si `AnswerPolicy` accepte une citation vers un document que le
demandeur n'a pas le droit de lire. Puis le retrouver : il existe déjà.

---

## Séance 3 — L'Application : huit ports, et pas un de plus

**Objectif.** Apprendre à justifier une frontière — pourquoi ici, pas un cran plus haut ni
plus bas — et à reconnaître ce qui n'en est pas une.

### Fichiers à projeter

| Fichier | Ce qu'on montre |
|---|---|
| `src/AssistantQR.Application/Ports/ILanguageModel.cs` | Commencer par celui-là. Une chaîne entre, une chaîne sort. Lire le bloc `<remarks>` en entier. |
| `src/AssistantQR.Application/Ports/IDocumentRepository.cs` | « Pourquoi pas plus bas / pourquoi pas plus haut ». Le modèle de raisonnement à reproduire. |
| `src/AssistantQR.Application/Ports/IClock.cs` | Le plus petit port. La taille d'une frontière ne dit rien de son utilité. |
| `src/AssistantQR.Application/Ports/IVectorIndex.cs` | Le point délicat : `SearchFilter` dans la signature. Annoncer la séance 6. |
| `src/AssistantQR.Application/Ports/IChunkingStrategy.cs` | Le port le plus **discutable** des huit. L'admettre en cours vaut mieux que le défendre. |
| `src/AssistantQR.Application/Model/EmbeddingVector.cs` | Note pédagogique : pourquoi ce type est en Application et pas en Domain. |
| `src/AssistantQR.Application/UseCases/AnswerQuestion/AnswerQuestionUseCase.cs` | L'orchestration, de bout en bout. Le point 5 : quand rien n'est lisible, **on n'appelle pas le modèle**. |
| `src/AssistantQR.Application/Retrieval/IRetrievalStrategy.cs` | Le contre-exemple : une interface qui n'est **pas** un port. |
| `tests/AssistantQR.Application.Tests/Doubles/` | Les doublures écrites à la main. `Application.Tests` ne référence pas `Infrastructure`. |

### Commandes à démontrer en direct

```bash
assistantqr ports
dotnet test tests/AssistantQR.Application.Tests
assistantqr ask "Y a-t-il une amende si je rends un livre en retard ?" --trace
```

Le `--trace` montre la chaîne complète : candidats bruts, écartés par le seuil, écartés par
l'accès, extraits réellement soumis au modèle, puis l'empreinte de configuration.

### Discussion à ouvrir

- Pourquoi `IPromptCatalog` est-il **synchrone** alors que les autres ports d'I/O ne le sont
  pas ?
- Pourquoi `IRetrievalStrategy` n'est-il pas un neuvième port ? Et le jeu de questions ?
- Faire proposer un neuvième port par la classe, puis le disqualifier ensemble avec le
  critère : *qu'est-ce que cette frontière rend remplaçable hors du système ?*

### Exercice

Écrire un `IEmbeddingService` de test qui renvoie toujours le même vecteur, et prédire ce
que devient la recherche. Vérifier.

---

## Séance 4 — L'Infrastructure et la racine de composition

**Objectif.** Montrer que « brancher les pièces » est une décision localisée, et qu'un faux
bien écrit est un outil de conception, pas un pis-aller.

### Fichiers à projeter

| Fichier | Ce qu'on montre |
|---|---|
| `src/AssistantQR.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`, méthode `AddPorts` | **La page à projeter.** Huit lignes de branchement. Cinq ports que le profil ne touche pas, trois qui basculent. |
| `src/AssistantQR.Infrastructure/Corpus/FrontMatterParser.cs` | Où « interne » devient `Internal`. Le dialecte du monde extérieur s'arrête ici. |
| `src/AssistantQR.Infrastructure/Embeddings/HashingEmbeddingService.cs` | FNV-1a, mots et bigrammes, normalisation L2. Trente lignes qui rendent tout le dépôt utilisable hors ligne. |
| `src/AssistantQR.Infrastructure/LanguageModels/ExtractiveLanguageModel.cs` | Le faux qui ignore délibérément température et graine, et qui **refuse** quand aucun extrait ne recoupe la question. Lire le `<remarks>`, puis `docs/adr/0005-doublure-modele-de-langue.md` : ce qu'une doublure peut simuler, et ce qu'elle ne peut pas. |
| `src/AssistantQR.Infrastructure/VectorIndex/InMemoryVectorIndex.cs` | Force brute, cosinus, `SearchFilter`. Comparer avec `python/embeddings_service/app/index.py`. |
| `src/AssistantQR.Cli/Program.cs` | Le fichier le plus pauvre du dépôt. La traduction des exceptions en phrases actionnables. |
| `src/AssistantQR.Cli/appsettings.json` | La configuration effective, et le commentaire sur la dimension 1024. |

### Commandes à démontrer en direct

```bash
assistantqr doctor
assistantqr corpus list
assistantqr index
assistantqr ports
assistantqr ports --llm replay                    # une seule ligne change
assistantqr ports --chunking whole-document       # une autre
assistantqr ask "Quels sont les horaires ?" --profil offlin   # faute de frappe : lire l'erreur
```

La faute de frappe donne :

> Option inconnue : « --profil ». Options acceptees ici : --profile, --embedding,
> --dimension, --chunking, --llm, --filter, --topk, --min-score, --temperature, --seed,
> --prompt, --prompt-version, --strict, --user, --clearance, --trace.

La dernière commande montre qu'une option inconnue est **refusée** au lieu d'être ignorée :
une faute de frappe silencieuse produirait une exécution aux valeurs par défaut, et un
résultat qu'on attribuerait au système plutôt qu'à la commande tapée.

### Discussion à ouvrir

- Pourquoi la racine de composition refuse-t-elle de laisser `HashingEmbeddingService`
  porter le nom configuré ? (Réponse en séance 5.)
- Pourquoi un profil inconnu lève-t-il au lieu de retomber sur `offline` ?
- `PathResolver` remonte les dossiers parents jusqu'à trouver `AssistantQR.sln`. Est-ce une
  bidouille acceptable ? Le code le documente comme telle.

### Exercice

Ajouter une quatrième stratégie de découpage (par exemple par phrase), l'enregistrer dans
`ChunkingStrategyFactory`, et mesurer son effet en séance 6.

---

## Séance 5 — L'asymétrie : le cœur du cours

**Objectif.** Établir expérimentalement que le modèle de langue est remplaçable et que le
modèle d'embeddings ne l'est pas. C'est la séance qui justifie toutes les précédentes.

### Fichiers à projeter

| Fichier | Ce qu'on montre |
|---|---|
| `tests/AssistantQR.Application.Tests/Demonstrations/AsymmetryTests.cs` | **Lire l'en-tête du fichier à voix haute avant toute exécution.** Les trois démonstrations, commentaire par commentaire. |
| `src/AssistantQR.Cli/Commands/LlmSwapDemo.cs` | Le second montage reçoit l'index du premier : l'égalité des récupérations est une propriété vérifiée, pas une coïncidence. |
| `src/AssistantQR.Cli/Commands/EmbeddingSwapDemo.cs` | L'étape 6 : « et si l'on oublie de réindexer ? ». |
| `python/embeddings_service/README.md`, section « la panne silencieuse » | `/index/search` ne refuse jamais un vecteur d'un autre modèle tant que la dimension correspond. |
| `README.md`, section 7 | Les deux scénarios, avec les sorties attendues. |

### Commandes à démontrer en direct

```bash
dotnet test tests/AssistantQR.Application.Tests --filter AsymmetryTests

assistantqr demo llm-swap
```

Faire lire la colonne « Récupération » : **douze fois « identique »**. Puis la conclusion.

```bash
assistantqr demo embedding-swap
```

La substitution se fait **à dimension égale** : `hashing-fake` contre `hashing-fake-b`,
1024 des deux côtés, même algorithme, seule la graine de projection change. Une ligne de
configuration, aucune exception, aucun test cassé.

Faire lire, dans l'ordre : la ligne `EmbeddingModel : hashing-fake → hashing-fake-b`, les
deux durées de réindexation, le taux de dérive (25 % sur ce dépôt), le tableau des natures
d'écart, puis **l'écart n° 3** — un refus devenu une réponse, qui parle de la fréquentation
du mercredi quand la question portait sur les effectifs du comptoir. Enchaîner sur
**l'étape 6** : l'index reconstruit par le second modèle, interrogé avec les vecteurs du
premier. « AUCUNE EXCEPTION N'A ETE LEVEE. » Puis le contraste `--strict`, que la commande
joue elle-même juste après : même index, même question, un booléen de configuration, et une
`InvalidOperationException` à la place d'une réponse plausible.

Terminer sur la conclusion : « Aucune erreur. Aucun test casse. »

Le détour bruyant, à montrer seulement si quelqu'un demande « et si les dimensions
diffèrent ? » :

```bash
assistantqr demo embedding-swap --dimensions 1024,384
```

L'index refuse la requête périmée. C'est le cas confortable, et le moins instructif : il
faut le dire en le montrant.

Si Ollama et le service Python sont disponibles, la version avec deux vrais modèles :

```bash
assistantqr demo embedding-swap --profile local --models qwen3-embedding:0.6b,bge-m3
```

Les deux modèles font 1024 dimensions. Rien ne proteste. Le parcours est le même que hors
ligne : l'installation confirme, elle n'ajoute pas.

Attention en séance : hors ligne, `assistantqr ask "..." --strict` **ne montre rien**.
Chaque commande reconstruit son index dans le processus, avec le modèle courant : les deux
noms coïncident, il n'y a donc rien à détecter. Le garde-fou ne se voit qu'à l'étape 6 de
`demo embedding-swap`, qui fabrique exprès un index périmé.

### Discussion à ouvrir

- Pourquoi le dépôt met-il `StrictIndexModelCheck` à `false` par défaut ? Trois raisons,
  section 7 du README.
- La détection repose sur une **comparaison de chaînes**. Que se passe-t-il si un faux
  emprunte le nom d'un vrai modèle ?
- Question à poser avant la démonstration, réponses au tableau : « lequel des deux modèles
  est le plus coûteux à changer ? » Comparer avec le résultat après.

### Exercice

Prendre un instantané avant la séance suivante :

```bash
assistantqr snapshot record reference
```

---

## Séance 6 — Les dilemmes, et l'évaluation par instantanés

**Objectif.** Terminer sur deux questions sans réponse. Une bonne architecture n'élimine
pas les arbitrages : elle les rend visibles, mesurables et attribuables.

### Fichiers à projeter

| Fichier | Ce qu'on montre |
|---|---|
| `tests/AssistantQR.Application.Tests/Retrieval/RetrievalStrategyTests.cs` | Le test central : la non-inclusion mutuelle des candidats, assertée. Lire le commentaire de classe, puis la « note honnête » en fin de test. |
| `src/AssistantQR.Application/Model/SearchFilter.cs` | Le dilemme dans un type de vingt lignes. |
| `src/AssistantQR.Cli/Commands/AccessFilterDemo.cs` | Le `<remarks>` de `DescribeDivergence` : le cas piégeux où la divergence est réelle dans le classement et invisible dans la réponse. |
| `docs/adr/0003-filtrage-acces.md` | Les deux positions dans leur forme la plus forte, et le fait gênant de la fin. |
| `prompts/answer-with-citations@1.0.0.md` et `@1.1.0.md` | Côte à côte. La règle métier n° 1 écrite en français dans un fichier Markdown. |
| `src/AssistantQR.Application/Model/ModelResponseParser.cs` | Le pendant exact du gabarit — et il n'est pas dans la même couche. |
| `prompts/README.md`, dernière section | Thèse A / thèse B / ce que chaque camp doit assumer. |
| `src/AssistantQR.Application/UseCases/Snapshots/ConfigurationFingerprint.cs` | Ce qu'un instantané enregistre à côté des réponses. |

### Commandes à démontrer en direct

**Le dilemme du filtrage :**

```bash
assistantqr demo access-filter \
  "Que devient le dossier d'un usager qui ne rend pas ses documents malgré les relances ?" \
  --user agent-accueil --clearance internal
```

Faire lire les deux colonnes ligne par ligne. Insister sur `inventaire-annuel#1`, qui
apparaît en pré-filtrage : il n'a pas été *retiré* par le post-filtrage, il n'a jamais
concouru. Puis compter les extraits soumis au modèle : **quatre en pré-filtrage, trois en
post-filtrage**. `contentieux-usagers#0` a occupé une place du top-k avant d'en être retiré,
et personne n'a repris cette place. C'est la forme forte du dilemme.

La forme faible, plus trompeuse, demande un top-k où les deux modes convergent :

```bash
ASSISTANTQR_PIPELINE__TOPK=10 assistantqr demo access-filter \
  "Y a-t-il une amende si je rends un livre en retard ?" \
  --user agent-accueil --clearance internal --min-score 0.15
```

La commande affiche alors deux cas : au top-k 9, huit extraits livrés contre neuf ; au
top-k 10, les mêmes neuf des deux côtés — alors qu'un document confidentiel a bel et bien
occupé une place, et que la place libérée par le pré-filtrage est revenue à un extrait que
le seuil a ensuite écarté. Même réponse, chemins différents. Demander à la classe ce qu'un
lecteur de la seule réponse en aurait conclu.

```bash
dotnet test tests/AssistantQR.Application.Tests --filter RetrievalStrategyTests
```

**La dérive, mesurée :**

```bash
assistantqr snapshot record decoupage-fixe --chunking fixed-600-100
assistantqr snapshot compare reference decoupage-fixe     # 58,3 % de dérive
assistantqr snapshot list
```

Aucune règle métier n'a bougé, les deux instantanés restent également sourcés, et sept
réponses sur douze ont changé — dont **quatre changements de décision** : un refus devenu
une réponse, ou l'inverse. C'est le principe CACE, et sa forme la plus gênante : le
découpage ne modifie pas seulement la formulation, il déplace la frontière entre répondre
et se taire.

**La frontière du prompt :**

```bash
assistantqr snapshot record prompt-strict --prompt-version 1.1.0
assistantqr snapshot compare reference prompt-strict
```

En profil `offline`, le résultat est **0 % de dérive**. Ne pas le cacher : le faire
expliquer par la classe. La réponse — `ExtractiveLanguageModel` ne lit pas les instructions
du prompt — est elle-même une leçon sur ce qu'un instantané mesure et ne mesure pas.

### Discussion à ouvrir — sans conclure

- Pré ou post-filtrage ? Faire défendre les deux camps, chacun dans sa forme la plus forte.
  Puis énoncer le fait gênant : sur l'ensemble final, le pré-filtrage ne peut jamais rendre
  un résultat strictement pire. L'argument du post-filtrage n'est donc pas « c'est mieux »,
  c'est « c'est plus sûr à long terme ».
- Le prompt appartient-il au domaine ou à l'infrastructure ? Faire lire les deux thèses.
  Demander : où le dépôt les a-t-il mis, et pourquoi ce choix est-il discutable ?
- Un `DriftRatio` de 0 % est-il une bonne nouvelle ?

### Clôture

Reprendre les trois lignes du fil rouge (README, section 1) et demander à la classe si
elles ont été démontrées. Puis la limite : **il n'y a pas de vérité terrain dans ce dépôt.**
Le taux de dérive mesure le changement, pas la qualité. Douze questions ne sont pas un jeu
d'évaluation. Ce que l'architecture rend possible, c'est de savoir **ce qui a changé** —
pas de savoir si c'est mieux.

---

## Annexe — Commandes de secours

Si une démonstration échoue en séance :

```bash
assistantqr doctor                    # d'abord, toujours
assistantqr ports                     # quel adaptateur est réellement branché
assistantqr help                      # syntaxe complète

# toute la chaîne, étape par étape
assistantqr ask "Y a-t-il une amende si je rends un livre en retard ?" --trace
```

Tout fonctionne en profil `offline`. Si une commande échoue en profil `local`, ajouter
`--profile offline` et poursuivre : les trois démonstrations sont conçues pour tourner sans
Ollama ni service Python, **et aucune ne perd son argument**. `demo embedding-swap` joue
hors ligne la substitution à dimension égale — deux faux de même dimension, graines de
projection différentes — donc la panne silencieuse elle-même, et non son détour bruyant.

Les instantanés produits en séance ne sont pas versionnés (`.gitignore`) : un
`snapshot record` de démonstration ne salit pas le dépôt.

---

## Annexe — Répartition horaire indicative

| Séance | Thème | Cours | Manipulation |
|---|---|---|---|
| 1 | Le problème | 1 h 30 | 1 h 30 |
| 2 | Le Domain | 2 h | 1 h |
| 3 | Les huit ports | 2 h | 1 h |
| 4 | Infrastructure et composition | 1 h 30 | 1 h 30 |
| 5 | L'asymétrie | 1 h 30 | 1 h 30 |
| 6 | Les dilemmes | 2 h | 1 h |

Total : 18 heures. Le chemin critique est 2 → 3 → 5 : si le volume est réduit, les séances
1, 4 et 6 se compressent, jamais celles-là.
