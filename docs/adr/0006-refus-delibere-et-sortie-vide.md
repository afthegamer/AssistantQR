# ADR 0006 — Un refus déclaré par le modèle n'est pas une sortie vide

- **Statut** : acceptée
- **Portée** : `src/AssistantQR.Domain/Answers/RefusalReason.cs`,
  `src/AssistantQR.Domain/Answers/DraftAnswer.cs`,
  `src/AssistantQR.Domain/Policies/AnswerPolicy.cs`,
  `src/AssistantQR.Application/Model/ModelResponseParser.cs`,
  `prompts/answer-with-citations@1.0.0.md`

---

## Contexte

Le gabarit de prompt **ordonne** au modèle d'émettre le marqueur `AUCUNE_REPONSE` quand les
extraits fournis ne permettent pas de répondre. Cet ordre n'est pas une astuce de
formatage : c'est la règle métier « refuser plutôt qu'inventer », écrite en langue
naturelle à destination d'un exécutant non déterministe. Un modèle qui émet ce marqueur
**obéit**.

Le problème est que cette obéissance et la panne la plus banale se ressemblent trait pour
trait à la frontière du port. `ILanguageModel` transporte une chaîne et rend une chaîne.
Trois situations arrivent au parseur sous une forme presque identique :

1. le modèle rend exactement `AUCUNE_REPONSE` — il a répondu, et sa réponse est « je ne
   peux pas répondre avec ces extraits » ;
2. le modèle rend `""` ou du blanc — délai dépassé, sortie tronquée, fournisseur muet, clé
   expirée, quota atteint ;
3. le modèle rend un texte — le cas ordinaire.

Les cas 1 et 2 produisent tous deux, une fois le marqueur retiré, **un texte vide**. Sans
information supplémentaire, `AnswerPolicy` ne voit qu'une seule chose : `DraftAnswer` dont
`Text` est vide. Elle est alors obligée de trancher au hasard, et le hasard qu'elle
choisissait était `ModelProducedEmptyAnswer` — « le modèle n'a rien produit d'exploitable ».

Cette phrase est fausse dans le cas 1. Et elle est fausse au pire endroit : dans le motif
que l'usager lit, dans le champ que l'instantané enregistre, dans le compteur qu'une
exploitation surveillerait. Le commentaire d'en-tête de `RefusalReason` pose la contrainte
qui rend l'arbitrage nécessaire — *un refus dont on ne saurait pas dire la cause serait
inexplicable à l'usager, donc inacceptable pour ce système*. Un refus dont on annonce la
**mauvaise** cause est pire encore : il est explicable, et il explique de travers.

---

## Décision

**Le refus déclaré par le modèle est une issue métier distincte, et elle porte son propre
motif.**

Trois pièces, une par couche, et aucune ne fait le travail d'une autre.

1. **Domain — `DraftAnswer` gagne un drapeau.**
   `DraftAnswer(string Text, IReadOnlyList<DocumentId> CitedDocumentIds, bool Declined = false)`.
   Le paramètre est optionnel et en dernier : toutes les constructions existantes à deux
   arguments continuent de compiler et signifient « non déclaré ». Deux fabriques nomment
   les deux silences : `DraftAnswer.Empty` (rien rendu) et `DraftAnswer.DeclinedByModel`
   (refus déclaré).

2. **Domain — `RefusalReason` gagne `ModelDeclinedToAnswer`**, et `AnswerPolicy.Decide`
   l'évalue en **règle 3**, après les deux refus du pipeline et **avant** le test du texte
   vide. L'ordre est porteur de sens : si aucun extrait n'était lisible, c'est le contrôle
   d'accès qui a tranché, pas le modèle — que l'on n'a d'ailleurs pas appelé. La fonction
   compte désormais huit règles dans un ordre fixe.

3. **Application — `ModelResponseParser` traduit, il ne juge pas.**
   Marqueur seul (aux blancs, à la casse et aux marqueurs de citation près) →
   `DraftAnswer.DeclinedByModel`. Chaîne nulle, vide ou blanche → `DraftAnswer.Empty`.
   Le parseur **sait traduire** le marqueur ; seul le Domain dit ce qu'il **signifie**.

La valeur numérique `6` placée en fin d'énumération est délibérée. `SnapshotEntry`
enregistre le motif sous forme de chaîne (`JsonStringEnumConverter`), donc renuméroter
aurait été sans conséquence pour les instantanés déjà écrits : le choix est **défensif**,
pas contraint. Rien n'oblige le rang d'une énumération à encoder un ordre d'évaluation, et
tout consommateur qui persisterait un jour la valeur entière — une colonne, une étiquette
de métrique — casserait en silence. On a préféré payer une lecture légèrement
contre-intuitive plutôt qu'un risque gratuit ; le commentaire du fichier le dit.

---

## Conséquences

### Ce que le dépôt gagne

**Deux nombres au lieu d'un.** « Le taux de refus a doublé » n'est pas un diagnostic. « Les
refus déclarés ont doublé, les sorties vides sont stables » désigne la recherche ou le
corpus ; « les sorties vides ont doublé, les refus déclarés sont stables » désigne le
fournisseur. Deux équipes différentes, deux corrections différentes. Un seul motif les
confondait dans une seule courbe.

**Un motif qui dit la vérité à l'usager.** « Le modèle a jugé lui-même que les extraits
fournis ne permettaient pas de répondre et a préféré s'abstenir plutôt que d'inventer »
répond à la question posée. « Le modèle n'a produit aucun texte exploitable » est un
bulletin de santé machine adressé à quelqu'un qui voulait les horaires du samedi.

**Une démonstration qui montre ce qu'elle prétend montrer.** La commande d'ouverture du
dépôt — `assistantqr ask "Quel est le tarif d'entrée à la piscine municipale ?"` — sert à
faire voir la règle « refuser plutôt qu'inventer » en action. Elle affichait un motif de
panne. Un support de cours qui illustre une règle métier par un message d'incident enseigne
exactement l'inverse de sa thèse.

**Des instantanés qui restent attribuables.** `demo llm-swap` oppose `extractive-fake` et
`replay` : la doublure de rejeu retombe sur le marqueur quand aucune entrée n'apparie le
prompt, et la table des issues affiche maintenant `Refus (ModelDeclinedToAnswer)` là où
elle affichait un motif technique. La dérive entre deux modèles se lit désormais comme ce
qu'elle est — un désaccord sur la suffisance des extraits — et non comme une panne
intermittente de l'un des deux.

### Ce que le dépôt paie

**Une frontière de plus à tenir juste.** Le drapeau ne vaut que ce que vaut la
reconnaissance du marqueur. `AUCUNE_REPONSE.` avec un point final n'est **pas** une
déclaration : ce texte suit le chemin ordinaire et se fait refuser à la règle 5, faute de
citation. C'est un choix — le marqueur imposé est exact — mais il faut l'assumer : un
modèle bavard sera classé « n'a pas cité » alors qu'il voulait refuser. Les tests
`Parse_RefusalMarkerWithExtraPunctuationOrWords_IsNotADeclaration` figent ce comportement
au lieu de le laisser dériver.

**Un adaptateur distrait peut effacer l'information.** Une implémentation future de
`ILanguageModel` qui renverrait `""` pour un refus déclaré ferait silencieusement retomber
le cas sur `ModelProducedEmptyAnswer`. La distinction est donc protégée par le parseur, pas
par le type : `DraftAnswer` accepte toujours qu'on ne renseigne pas le drapeau.

**Un motif de plus dans une énumération que le cours présente comme exhaustive.** Sept
plutôt que six. C'est le prix de la précision, et il est visible dans le tableau de la
section 4 du README.

---

## Alternatives écartées

**Laisser l'adaptateur traduire `AUCUNE_REPONSE` en chaîne vide, et n'avoir qu'un seul
motif.** C'est l'alternative sérieuse, et elle avait de vrais arguments : un motif de moins,
un champ de moins sur `DraftAnswer`, une branche de moins dans `AnswerPolicy`, et aucun
risque qu'un adaptateur oublie de positionner un drapeau — puisqu'il n'y en aurait pas.
Elle se défend aussi sur le fond : les deux cas produisent bien, du point de vue du port,
la même chose, à savoir aucun texte exploitable ; et le système se comporterait
identiquement dans les deux, puisqu'il refuse.

Écartée, parce que « le système se comporte identiquement » n'est vrai que de la décision,
jamais de son explication — et que ce dépôt tient l'explication pour une partie de la
décision. Quatre pertes, de la plus concrète à la plus structurelle.

1. **On perd la mesure.** Les deux causes deviennent un seul compteur, et ce compteur ne
   décide de rien : sa hausse peut vouloir dire « le corpus ne couvre plus les questions
   posées » comme « le fournisseur est instable ». Personne ne sait à qui envoyer le
   ticket. Une observabilité qui ne discrimine pas ne sert qu'à confirmer qu'un problème
   existe, ce que l'on savait déjà.

2. **On perd la vérité du message.** Le refus rendu à l'usager annoncerait une panne qui
   n'a pas eu lieu. Et la même phrase serait enregistrée dans l'instantané, donc relue en
   séance six mois plus tard comme une trace d'incident.

3. **On perd la démonstration.** Le cœur du cours est que la règle « refuser plutôt
   qu'inventer » est du **métier** et qu'elle vit dans le Domain. La donner à voir sous le
   motif `ModelProducedEmptyAnswer`, c'est la présenter comme un effet de bord d'une panne.
   Le dépôt enseignerait le contraire de ce qu'il affirme, et le ferait dans sa toute
   première commande d'exemple.

4. **On perd le droit de trancher.** C'est la perte structurelle, et la seule qui soit
   irréversible. Traduire le marqueur en `""` **ressemble** à du formatage ; c'est en
   réalité une décision sémantique — « obéir au prompt, c'est ne rien produire » — prise
   par un adaptateur. Une fois cette traduction faite, l'information n'existe plus : aucun
   test du Domain, aucune politique, aucune règle ajoutée plus tard ne peut la reconstruire,
   parce qu'elle a été détruite **avant** la frontière. C'est précisément le glissement que
   tout le dépôt cherche à rendre visible : une règle métier qui disparaît dans un
   composant technique, sans que rien ne le signale.

**Faire remonter le marqueur brut jusqu'au Domain** et laisser `AnswerPolicy` comparer la
chaîne à `"AUCUNE_REPONSE"`. Écartée : le Domain connaîtrait alors la formulation exacte
d'un prompt rédigé en français pour un fournisseur donné. Le jour où le gabarit passe à
`NO_ANSWER` ou à un champ JSON structuré, c'est le cœur métier qu'il faut modifier. Le
booléen porte le **sens** sans porter la **formulation** — c'est exactement ce qu'une
frontière est censée faire.

**Un type `ModelOutcome` (répondu / déclaré / muet) à la place du booléen.** Plus
extensible, et probablement le bon choix le jour où un troisième état apparaîtra — sortie
tronquée, refus de sécurité du fournisseur, filtre de contenu déclenché. Écartée
aujourd'hui parce qu'avec deux états, un booléen nommé plus deux fabriques nommées se lisent
mieux qu'une énumération à deux valeurs. La décision est réversible sans toucher au reste.

**Un `RefusalReason` par cause technique** (`ModelTimedOut`, `ModelTruncated`,
`ProviderUnavailable`). Tentant, et faux ici : ces causes ne sont pas des décisions du
Domain, ce sont des incidents que les adaptateurs traduisent déjà en exceptions typées avec
un message actionnable (`LanguageModelException`, voir `Program.Explain`). Les faire entrer
dans l'énumération des refus reviendrait à confondre « le système a décidé de ne pas
répondre » et « le système n'a pas pu fonctionner » — soit la faute même que cette ADR
corrige, commise dans l'autre sens.

---

## Références

- `src/AssistantQR.Domain/Answers/RefusalReason.cs` — les sept motifs, et le commentaire sur le rang 6
- `src/AssistantQR.Domain/Answers/DraftAnswer.cs` — le drapeau `Declined` et les deux fabriques
- `src/AssistantQR.Domain/Policies/AnswerPolicy.cs` — la règle 3, et pourquoi elle vit là
- `src/AssistantQR.Application/Model/ModelResponseParser.cs` — la traduction du marqueur
- `tests/AssistantQR.Domain.Tests/AnswerPolicyTests.cs` — `Decide_RefusDeclareParLeModele_RefuseModelDeclinedToAnswer`, `Decide_RefusDeclareAvecDuTexte_RefuseQuandMemeModelDeclinedToAnswer`, `Decide_TexteVideSansDeclaration_RefuseModelProducedEmptyAnswer`
- `tests/AssistantQR.Application.Tests/Model/ModelResponseParserTests.cs` — `Parse_RefusalMarker_ProducesADeclinedDraft`, `Parse_EmptyResponse_ProducesANonDeclinedDraft`, `Parse_RefusalMarkerWithExtraPunctuationOrWords_IsNotADeclaration`
- `docs/adr/0005-doublure-modele-de-langue.md` — pourquoi la doublure refuse, et ce que ce refus vaut comme ligne de base
- README racine, section 4 — le tableau des sept motifs dans l'ordre d'évaluation
