# ADR 0003 — Filtrage d'accès : pré et post, configurable

- **Statut** : acceptée
- **Portée** : `src/AssistantQR.Application/Retrieval/`,
  `src/AssistantQR.Application/Model/SearchFilter.cs`,
  `src/AssistantQR.Application/Ports/IVectorIndex.cs`,
  `src/AssistantQR.Domain/Policies/AccessPolicy.cs`,
  `python/embeddings_service/app/index.py`

---

## Contexte

Chaque document du corpus porte un niveau d'accès (`public`, `internal`, `confidential`) et
chaque demandeur une habilitation. La règle métier n° 3 est simple : *on ne s'appuie jamais
sur un document qu'on n'a pas le droit de lire*. Sa mise en œuvre ne l'est pas.

La recherche vectorielle rend les **k plus proches voisins**. Deux placements du filtre
sont possibles, et **ils ne donnent pas le même résultat** :

- **Pré-filtrage** — l'index ne classe que les documents lisibles, puis rend les k
  meilleurs de cet ensemble ;
- **Post-filtrage** — l'index classe tout, rend les k meilleurs, et l'application retire
  ensuite ce qui est interdit.

Ce n'est pas une différence de style. `k` est une contrainte de **cardinalité** appliquée à
un classement : filtrer avant, c'est classer un autre ensemble, et les places libérées par
les documents interdits sont reprises par des documents qui n'auraient jamais atteint le
top-k. **Aucun des deux ensembles de candidats n'est inclus dans l'autre.**

Le corpus est conçu pour que la question se pose réellement : `retards-amendes` (public),
`gestion-retards-interne` (interne) et `contentieux-usagers` (confidentiel) partagent le
vocabulaire du retard, de la relance et de l'usager. Sans ce recouvrement lexical, le
dilemme ne se déclencherait jamais et la démonstration serait un artefact de laboratoire.

---

## Décision

**Le dépôt implémente les deux, le mode est un réglage de configuration, et le défaut est
`post`.**

- `AccessFilterMode { Pre, Post }` dans `Application/Configuration/`.
- Deux implémentations de `IRetrievalStrategy` dans `Application/Retrieval/`, choisies par
  `RetrievalStrategyFactory.Create(mode, index)`.
- `SearchFilter` (`None` | `MaxAccessLevel`) traverse la frontière `IVectorIndex` et fait
  donc entrer, **volontairement et à découvert**, une notion métier dans le contrat d'un
  composant technique.
- **`AccessPolicy` repasse sur les résultats dans les deux modes.** En mode `pre`, cette
  passe ne doit rien retirer : si elle retire quelque chose, l'adaptateur est défaillant.
- Le mode figure dans `ConfigurationFingerprint`, donc dans chaque instantané.
- `assistantqr demo access-filter "<question>"` exécute la même question dans les deux
  modes, à un ou deux top-k selon ce qu'il faut pour exhiber la divergence, et affiche les
  classements côte à côte : rendu par l'index, soumis au modèle, écarté par le seuil,
  écarté par le contrôle d'accès.

`IRetrievalStrategy` **n'est pas un neuvième port** : ses deux implémentations vivent dans
l'Application et ne parlent qu'à `IVectorIndex`. C'est une stratégie interne. Le contraste
avec les huit ports est pédagogique et voulu.

---

## Conséquences

### Ce que le dépôt gagne

**L'arbitrage se mesure au lieu de se décréter.** Sur la question pivot du jeu
(`Que devient le dossier d'un usager qui ne rend pas ses documents malgré les relances ?`,
posée par un agent `internal`), les deux modes livrent des classements différents :

```
PRE-filtrage                                     | POST-filtrage
-------------------------------------------------+-------------------------------------------------
0.483 gestion-retards-interne#0 (internal)       | 0.483 gestion-retards-interne#0 (internal)
0.320 retards-amendes#0 (public)                 | 0.320 retards-amendes#0 (public)
0.269 budget-acquisitions#4 (internal)           | 0.297 contentieux-usagers#0 (confidential)
0.257 inventaire-annuel#1 (internal)             | 0.269 budget-acquisitions#4 (internal)
→ 4 extraits soumis au modele                    | → 3 extraits soumis au modele
```

`inventaire-annuel#1` n'est jamais entré dans le top-k du post-filtrage. Il n'était pas
« retiré » : il n'a jamais concouru. Symétriquement, `contentieux-usagers#0` a occupé une
place du top-k en post-filtrage avant d'en être retiré, et **personne n'a repris cette
place** : le modèle reçoit trois extraits au lieu de quatre.

**Le dilemme a deux formes, et la commande montre les deux.** La forme **forte** est
celle ci-dessus : les deux modes ne livrent pas le même nombre d'extraits. La forme
**faible**, et bien plus trompeuse : les extraits livrés coïncident, la divergence n'existe
que dans le classement intermédiaire — un document interdit a bien occupé une place, et la
place libérée par le pré-filtrage est revenue à un extrait que le seuil de score a ensuite
écarté. Une lecture de la seule réponse conclurait alors à tort que le mode est sans
conséquence. `AccessFilterDemo` cherche donc, à chaque exécution et en descendant depuis le
top-k configuré, la première valeur qui exhibe la forme forte, affiche les deux cas quand
elles diffèrent, et **dit franchement** quand il n'en trouve pas :

> Aucune valeur de topK entre 1 et 4 ne fait livrer au modele deux ensembles d'extraits
> differents. Autant le dire plutot que de mettre en scene un dilemme absent.

Ce top-k n'est pas codé en dur : il dépend de la question, du corpus, du découpage, du
modèle d'embeddings et du seuil. Une constante écrite dans le code aurait continué
d'affirmer la divergence longtemps après avoir cessé de la produire.

**La non-inclusion mutuelle est assertée, pas affirmée.**
`tests/AssistantQR.Application.Tests/Retrieval/RetrievalStrategyTests.cs` :

```csharp
Assert.False(postCandidates.IsSubsetOf(preCandidates));
Assert.False(preCandidates.IsSubsetOf(postCandidates));
```

**La défense en profondeur est testée, y compris son cas de déclenchement.** Le test
`RetrieveAsync_PreFilterAndTheIndexIgnoresTheFilter_DefenseInDepthStillExcludes` fabrique
un index qui accepte le plafond d'habilitation puis l'ignore — bogue, mauvaise traduction
du niveau, régression d'adaptateur. Aucun test du Domain ne peut atteindre ce code. La
seule protection est que l'Application repasse par `AccessPolicy`, et le test le vérifie.

**La sécurité ne dépend pas du mode choisi.** `AnswerPolicy` reste le dernier rempart : si
le modèle cite un document qu'il a vu mais que le demandeur ne pouvait pas lire, le refus
est `ModelCitedForbiddenDocument`. C'est un refus, pas une prévention — nuance qui compte.

### Ce que le dépôt paie

**Deux chemins de code à maintenir**, dont un — `pre` — qui n'est pas le défaut et sera
donc moins exercé en pratique. Les tests le couvrent explicitement pour cette raison.

**Le contrat de `IVectorIndex` est impur, et le restera.** `SearchAsync` accepte un
`SearchFilter`. C'est une règle métier dans la signature d'un composant technique. Le
commentaire de l'interface l'assume au lieu de le masquer. Le service Python porte la même
impureté : `/index/search` accepte `max_access_level`.

**Le mode devient une variable de dérive de plus.** Comparer deux instantanés pris dans
deux modes différents ne prouve rien sur autre chose que le mode.
`ConfigurationFingerprint.DifferencesWith` le rappelle en tête de chaque comparaison.

**Le pré-filtrage duplique une règle de sécurité hors du Domain.** Le jour où l'index
change de fournisseur, la traduction `AccessLevel` → dialecte de la base devient un point
de défaillance que les tests du cœur ne voient pas.

### Le fait gênant, à énoncer honnêtement

Sur l'ensemble **final** — après filtrage — l'inclusion n'est pas symétrique et ne peut pas
l'être : « les k meilleurs parmi les lisibles » contient nécessairement « les lisibles
parmi les k meilleurs ». **Le pré-filtrage ne peut donc jamais rendre un résultat
strictement pire.** Il ne se paie pas en pertinence ; il se paie en architecture.

Cela affaiblit l'argument naïf du post-filtrage (« c'est aussi bon et plus propre ») : ce
n'est pas aussi bon. L'argument valable du post-filtrage est différent — *une règle métier
écrite à deux endroits dérivera, et le coût de cette dérive est une faille, pas une
réponse appauvrie*. C'est un pari sur le long terme, pas un avantage immédiat, et il faut
le présenter comme tel.

---

## Alternatives écartées

**Post-filtrage seul, `SearchFilter` supprimé de `IVectorIndex`.**
Le contrat de l'index redeviendrait purement technique et la règle métier vivrait en un
seul endroit. Écartée pour deux raisons. La première est pédagogique : le dilemme est le
cœur de la séance, et un dépôt qui a déjà tranché ne le fait pas discuter. La seconde est
factuelle : la plupart des bases vectorielles réelles proposent un filtre pré-classement,
et prétendre que la question ne se pose pas serait mentir sur l'état de l'art.

**Pré-filtrage seul, `AccessPolicy` réduite à un rôle documentaire.**
Écartée parce qu'elle rend la sécurité du système dépendante de la correction d'un
adaptateur, sans aucun test du Domain pour la couvrir. Le test
`DefenseInDepthStillExcludes` existe précisément pour montrer ce qui se passe alors.

**Surdimensionner le top-k en post-filtrage** (chercher `k × 3`, filtrer, garder `k`).
C'est la solution pragmatique la plus répandue, et elle est bonne en production. Écartée
ici parce qu'elle **masque le dilemme sans le résoudre** : la divergence subsiste, elle
devient simplement moins fréquente. Un cours dont la démonstration ne se déclenche qu'une
fois sur vingt n'est pas un cours. À noter qu'elle a aussi un coût réel — plus de
documents interdits transitent par la mémoire de l'application, pas moins.

**Filtrage par index séparé, un par niveau d'accès.**
Trois index, on interroge ceux que l'habilitation autorise, on fusionne les classements.
Sûr par construction, et sans règle métier dans le contrat de recherche. Écartée pour le
coût : trois index à réindexer, une fusion de classements à écrire et à justifier, une
explosion combinatoire dès qu'on ajoute une dimension d'autorisation (service, projet,
géographie). C'est une réponse défendable dans un vrai système ; c'est trop de machinerie
pour un support de cours.

**Filtrage dans le prompt** (« ne cite pas les documents marqués confidentiel »).
Mentionnée pour être écartée nettement : cela revient à confier une règle de sécurité à un
composant non déterministe. C'est l'anti-pattern que tout le dépôt cherche à rendre
visible.

---

## Références

- `src/AssistantQR.Application/Retrieval/PreFilterRetrievalStrategy.cs`, `PostFilterRetrievalStrategy.cs`
- `src/AssistantQR.Application/Model/SearchFilter.cs` — le commentaire du type pose le dilemme
- `tests/AssistantQR.Application.Tests/Retrieval/RetrievalStrategyTests.cs` — le test central
- `src/AssistantQR.Cli/Commands/AccessFilterDemo.cs` — la démonstration en direct
- README racine, section 9a — les deux positions dans leur forme la plus forte
