# ADR 0002 — `EmbeddingVector` vit en Application, pas en Domain

- **Statut** : acceptée
- **Portée** : `src/AssistantQR.Application/Model/EmbeddingVector.cs`,
  `src/AssistantQR.Domain/Evidence/EvidenceFragment.cs`,
  `tests/AssistantQR.Domain.Tests/ArchitectureTests.cs`

---

## Contexte

L'assistant retrouve les passages pertinents par recherche vectorielle. Il faut donc un
type qui porte un vecteur, sa dimension et le calcul de similarité cosinus. La question est
de savoir dans quelle couche il vit.

L'argument en faveur du Domain est réel et mérite d'être posé sérieusement : le vecteur est
au cœur du fonctionnement, il traverse presque tout le pipeline, il apparaît dans la
signature de trois des huit ports, et le mettre « en bas » est le réflexe naturel quand on
range par ordre de généralité plutôt que par ordre de dépendance.

Le Domain de ce projet est par ailleurs contraint : zéro dépendance, pas d'I/O, pas
d'horloge, testable sans réseau. Un `readonly record struct` portant un
`ReadOnlyMemory<float>` respecterait ces contraintes sans difficulté — la BCL suffit. Le
choix n'est donc pas imposé par la technique.

---

## Décision

**`EmbeddingVector` est déclaré dans `AssistantQR.Application.Model`. Le Domain ignore
totalement son existence.**

Corollaires appliqués :

- `EvidenceFragment` (Domain) **ne porte pas de score de similarité**. Le score est un
  artefact de la mécanique de recherche : il vit dans `ScoredFragment`
  (`Application/Model/ScoredFragment.cs`), qui enveloppe le fragment du Domain.
- `Chunk`, `IndexedChunk`, `EmbeddingModelDescriptor`, `IndexMetadata` et `SearchFilter`
  sont également en Application, pour la même raison.
- `ArchitectureTests` interdit le fragment `Vector` dans toute signature publique du Domain,
  et un test témoin vérifie que le détecteur détecte.

---

## Conséquences

### Le critère qui a tranché

La question n'est pas « ce type est-il central ? » mais **« ce type survivrait-il au
remplacement du mécanisme de recherche ? »**.

Le métier de cette application est : *répondre à une question en citant des documents que
le demandeur a le droit de lire*. Cette phrase ne mentionne aucun vecteur. Elle serait
inchangée si la recherche se faisait par BM25, par index inversé, par expressions
régulières, ou par un bibliothécaire humain qui apporte les bons dossiers. Le vecteur
n'est pas un concept du métier : c'est **le mécanisme de recherche qu'on a choisi**.

Symétriquement, `Answer`, `Citation`, `AccessLevel` et `AnswerPolicy` survivraient à
n'importe lequel de ces remplacements. Ils sont donc du Domain.

### Positives

**Le Domain reste testable sans la moindre notion de similarité.** `AnswerPolicy.Decide`
prend une liste de `EvidenceFragment` et un `DraftAnswer`. Elle ne sait pas d'où viennent
les fragments, ni pourquoi ceux-là plutôt que d'autres. Ses 176 tests s'écrivent avec des
fragments construits à la main.

**Le contrôle négatif est facile à faire en cours.** On demande aux étudiants : « où
mettriez-vous le score de similarité ? ». La réponse spontanée est « sur
`EvidenceFragment`, c'est plus pratique ». Puis on ouvre `ArchitectureTests` et on
constate que ce choix rendrait le Domain dépendant d'une notion de classement — donc d'une
mécanique de recherche — et qu'un changement de mécanique ferait bouger un type métier.

**La frontière du Domain est vérifiable mécaniquement, pas par discipline.** Le fragment
`Vector` est dans la liste noire de `ArchitectureTests`, avec `Http` et `Json`. Le test
échoue à la compilation suivante, pas six mois plus tard en revue.

**Le remplacement de mécanisme reste local.** Ajouter un re-classement, un score hybride
BM25 + cosinus, ou une expansion de requête se fait entièrement dans `Application` et
`Infrastructure`. Aucun type du Domain n'a de raison de bouger.

### Négatives, assumées

**Une enveloppe de plus.** `ScoredFragment(EvidenceFragment Fragment, double Score)` est un
type qui n'existerait pas si le score était sur le fragment. On paie une indirection, et
tout le code de trace écrit `scored.Fragment.DocumentId` au lieu de `fragment.DocumentId`.

**Une frontière contre-intuitive au premier abord.** Le vecteur est le composant le plus
« technique » du système et il est en Application, pas en Infrastructure. Cela surprend :
« technique » ne veut pas dire « infrastructure ». `EmbeddingVector` ne fait aucune I/O ;
il est le vocabulaire que l'Application impose à ses adaptateurs. Cette distinction —
*mécanisme choisi* contre *détail d'exécution* — demande à être expliquée explicitement en
cours, sinon elle passe pour de l'arbitraire.

**La règle est plus facile à énoncer qu'à appliquer sur les cas limites.** `Chunk` a été
long à placer : un morceau de document ressemble à un concept métier. Il est en Application
parce que le découpage est une décision de pertinence de recherche (principe CACE), pas une
règle de la médiathèque. Le commentaire du fichier le dit.

---

## Alternatives écartées

**`EmbeddingVector` dans le Domain, avec le score sur `EvidenceFragment`.**
La plus tentante, parce qu'elle simplifie le code appelant. Écartée parce qu'elle rendrait
le Domain porteur d'une notion de classement, donc d'une mécanique de recherche. Le premier
symptôme serait bénin — `EvidenceFragment.Score` nullable, valant `null` quand on construit
un fragment à la main dans un test. Le second le serait moins : quelqu'un finirait par
écrire une règle métier qui compare des scores, et à ce moment-là `AnswerPolicy`
dépendrait d'un seuil de similarité. Le seuil `MinScore` est un réglage de recherche ; il
vit dans `PipelineOptions`, et il n'a rien à faire dans une politique métier.

**`EmbeddingVector` dans l'Infrastructure.**
Cohérent avec l'intuition « c'est technique, donc c'est en bas à droite ». Impossible en
pratique : `IEmbeddingService`, `IVectorIndex` et `IndexedChunk` sont dans l'Application et
en ont besoin dans leur signature. Un port ne peut pas dépendre de ses implémentations,
sinon l'inversion de dépendance disparaît et l'Application redevient cliente de
l'Infrastructure.

**`float[]` nu, sans type dédié.**
Écartée pour trois raisons. Aucune protection contre la comparaison de vecteurs de
dimensions différentes — un cas que l'on veut voir lever avec un message explicite
(`Comparaison impossible : dimensions différentes`). Ce cas-là n'est pas le scénario B :
c'est son détour bruyant, accessible par `demo embedding-swap --dimensions 1024,384`. Le
scénario B, lui, se joue à dimension égale, où ce garde-fou ne se déclenche jamais. Aucun
endroit naturel où loger `CosineSimilarity`, qui finirait dans une classe utilitaire
statique. Et un tableau est mutable : le passer entre couches revient à partager un état
modifiable à travers une frontière.

**Un `EmbeddingVector` générique paramétré par la dimension** (`EmbeddingVector<D1024>`).
Rend l'incompatibilité de dimensions détectable à la compilation. Écartée parce que la
dimension est une donnée de **configuration**, connue à l'exécution, et que le typage
statique ne protégerait de rien dans le cas qui compte : deux modèles de **même**
dimension, incompatibles quand même. La panne du scénario B passerait le compilateur avec
ou sans ce raffinement — et le raffinement donnerait la fausse impression qu'elle est
couverte.

Ce cas n'est plus une hypothèse de raisonnement : l'Infrastructure fournit deux faux
d'embeddings de même dimension, `hashing-fake` et `hashing-fake-b`, qui ne diffèrent que
par la graine de projection. `assistantqr demo embedding-swap` les substitue l'un à l'autre
en dimension 1024, hors ligne, et mesure la dérive — sans qu'aucune exception ne soit levée
ni qu'aucun type ne change. Un `EmbeddingVector<D1024>` aurait compilé, et se serait tu.

---

## Références

- `src/AssistantQR.Application/Model/EmbeddingVector.cs` — le commentaire du type porte la même justification
- `src/AssistantQR.Domain/Evidence/EvidenceFragment.cs` — note pédagogique sur l'absence de score
- `tests/AssistantQR.Domain.Tests/ArchitectureTests.cs` — la règle, exécutable
- README racine, section 3 (règle d'inversion) et section 5 (les huit ports)
