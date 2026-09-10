# ADR 0005 — La doublure de modèle de langue est extractive, et elle refuse

- **Statut** : acceptée
- **Portée** : `src/AssistantQR.Infrastructure/LanguageModels/ExtractiveLanguageModel.cs`,
  `src/AssistantQR.Infrastructure/LanguageModels/ReplayLanguageModel.cs`,
  `tests/AssistantQR.Infrastructure.Tests/LanguageModels/ExtractiveLanguageModelTests.cs`,
  `prompts/answer-with-citations@1.0.0.md`

---

## Contexte

Le port `ILanguageModel` transporte une chaîne et rend une chaîne. Le profil `offline` doit
en fournir une implémentation qui ne sorte pas du processus : sans elle, aucune commande du
dépôt ne répond sans Ollama, et le support de cours devient une promesse.

Une doublure de modèle de langue n'a que trois comportements possibles.

1. **Elle répond toujours.** Elle prend les premiers extraits venus et les recite. Simple,
   déterministe, et fausse sur le point qui compte : le gabarit de prompt ORDONNE au modèle
   d'émettre le marqueur de refus quand les extraits ne permettent pas de répondre. Une
   doublure qui répond toujours est une doublure qui désobéit systématiquement.
2. **Elle refuse toujours.** Aucun intérêt : le pipeline ne se voit jamais fonctionner.
3. **Elle décide.** Elle applique un critère explicite pour trancher entre répondre et
   refuser.

Le choix n'est pas cosmétique. Ce que la doublure décide devient la ligne de base de tous
les instantanés hors ligne, donc la référence à laquelle la dérive d'un vrai modèle est
comparée. Une ligne de base qui répond toujours rend invisible la moitié du comportement
attendu — celle où le système se tait.

---

## Décision

**`ExtractiveLanguageModel` extrait au lieu d'engendrer, et il refuse quand aucun extrait
ne recoupe la question.**

Le mécanisme, en trois temps :

1. Il relit le bloc d'extraits injecté dans le prompt (le format produit par
   `EvidenceFormatter` : `[identifiant] Titre (niveau)`, puis le texte) et la question,
   repérée par le titre de section qui précède `{{question}}` dans les gabarits.
2. Il réduit la question à ses mots significatifs — minuscules, accents repliés, quatre
   lettres au moins, mots vides et mots interrogatifs écartés — et ne retient un extrait
   que si **deux** de ces mots se retrouvent **au même endroit** de son bloc : soit dans
   l'en-tête, soit dans le texte, jamais en additionnant un mot de l'un et un mot de
   l'autre.
3. S'il ne reste aucun extrait, il émet `ModelResponseParser.RefusalMarker`
   (`AUCUNE_REPONSE`). Sinon il recopie la première phrase des deux premiers extraits
   retenus, chacune suivie de son identifiant entre crochets.

Il ignore délibérément température et graine, et le documente. `ReplayLanguageModel` reste
la seconde doublure : elle rejoue des réponses figées et retombe sur `AUCUNE_REPONSE`
lorsqu'aucune entrée n'apparie le prompt.

Conséquence observable en ligne de commande, sans rien installer :

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

Quatre morceaux ont franchi le seuil de similarité — un seuil ne connaît pas le hors-sujet
— le modèle a bien été appelé, et il a refusé. Le motif est `ModelDeclinedToAnswer` et non
`ModelProducedEmptyAnswer` : la doublure a **obéi** au gabarit, elle n'est pas tombée en
panne. Les deux silences sont distingués depuis
`docs/adr/0006-refus-delibere-et-sortie-vide.md`.

---

## Conséquences

### Ce que le dépôt gagne

**« Refuser plutôt qu'inventer » cesse d'être une règle qu'on lit pour devenir une règle
qu'on voit.** Avant, elle n'était démontrable que par les tests du Domain : `AnswerPolicy`
refusait un brouillon vide, mais rien ne produisait jamais ce brouillon vide en profil
`offline`. La règle était vraie et invisible. Une règle invisible ne s'enseigne pas.

**La ligne de base couvre les deux issues.** Sur les douze questions de
`snapshots/questions.json`, l'instantané hors ligne contient aujourd'hui des réponses et
des refus. Un instantané où tout est répondu ne peut pas faire apparaître un
`RefusalChanged` : la moitié la plus intéressante de la dérive serait indétectable par
construction.

**La doublure simule l'obéissance au prompt, donc rend le port honnête.** Le gabarit
demande un refus ; la doublure en produit un. Le contraste avec un vrai modèle reste entier
— lui obéit *ou pas*, et rien dans `ILanguageModel` ne l'y contraint.

**La garantie métier ne bouge pas d'un pouce.** `AnswerPolicy` reste le filet : elle
vérifie que les identifiants cités existent dans les extraits fournis et sont lisibles par
le demandeur. Le critère de la doublure ne remplace pas la politique ; il rend seulement la
doublure représentative du comportement attendu. Toutes les garanties du système restent
**après** le port, jamais dedans.

### Ce que le dépôt paie

**Un critère de pertinence vit maintenant dans une doublure.** C'est une heuristique de
plus à maintenir, avec ses constantes — deux mots, quatre lettres, une liste de mots vides.
Elle est couverte par dix-sept méthodes de test, dont les cas de bord qui ont motivé chaque
constante.

**La doublure lit le prompt pour y retrouver la question.** Le port ne transporte qu'une
chaîne : la question a déjà fondu dans le gabarit quand elle arrive. Le seul repère est le
titre de section qui précède `{{question}}`. Élargir le contrat du port pour transporter la
question à part serait plus solide, mais déformerait le port pour les besoins d'une
doublure — un vrai fournisseur, lui, ne recevra jamais qu'un prompt. Le couplage est assumé,
et la dégradation prévue : question introuvable ou sans mot significatif, on revient aux
deux premiers extraits plutôt que de tout refuser en bloc.

**Le seuil de refus devient une variable de dérive de plus.** Changer le découpage déplace
les mots présents dans chaque morceau, donc le recoupement, donc la frontière entre réponse
et refus. La comparaison `paragraph` → `fixed-600-100` produit quatre `RefusalChanged` sur
douze questions. Ce n'est pas un défaut de la mesure : c'est le principe CACE, appliqué à un
composant qui n'est même pas un modèle.

### Ce que cette doublure NE peut PAS simuler

Il faut le dire en séance avant d'exécuter quoi que ce soit, sinon la démonstration ment.

**La compréhension.** Le critère est un recoupement de chaînes de caractères. Il ne sait pas
qu'une question porte sur un sujet ; il sait que deux suites de lettres apparaissent des
deux côtés. Conséquences directes, et toutes observables :

- **Il refuse une question reformulée avec des synonymes.** « tarif » et « prix », « agent »
  et « employé » sont des dimensions étrangères l'une à l'autre — pour le faux d'embeddings
  comme pour cette doublure.
- **Il accepte un extrait qui partage le vocabulaire sans répondre.** Les entrées 8 et 9 de
  `snapshots/questions.json` le montrent : le montant demandé n'existe que dans un document
  confidentiel, la doublure cite un document interne qui parle du même contrat sans donner
  le chiffre, et produit une réponse sourcée, bien formée, et à côté de la question.
- **Il ne synthétise pas.** Deux extraits complémentaires restent deux premières phrases
  juxtaposées. Aucune reformulation, aucune mise en relation, aucune hiérarchie entre un
  fait principal et un détail.
- **Il n'obéit à aucune autre consigne du gabarit.** Longueur, ton, une citation par
  affirmation, suppression des affirmations invérifiables : rien de tout cela n'est
  appliqué. C'est pourquoi comparer `answer-with-citations@1.0.0` et `@1.1.0` donne **0 %
  de dérive** en profil `offline`. Le chiffre est juste ; ce qu'il mesure, c'est
  l'indifférence de la doublure aux instructions, pas la neutralité de la version 1.1.0.

La conclusion à faire passer : cette doublure est **représentative** du comportement d'un
modèle obéissant, elle n'en est pas un **substitut**. Elle sert à démontrer une
architecture, jamais à évaluer une qualité de réponse. Le jour où l'on veut savoir si le
système répond *bien*, il faut un vrai modèle et une vérité terrain — le dépôt n'a ni l'un
ni l'autre, et le dit.

---

## Alternatives écartées

**La doublure qui répond toujours** (le comportement d'origine : les deux premiers extraits,
sans condition). Écartée parce qu'elle produit une ligne de base sans aucun refus d'origine
modèle, et parce qu'elle contredit le gabarit qu'elle est censée suivre. Son seul avantage —
une sortie plus courte à expliquer — ne vaut pas la perte de la moitié du comportement.

**Un seuil proportionnel** (exiger la moitié des mots significatifs de la question). Plus
fin en apparence, et cassant sur les questions composées : « Quels sont les horaires
d'ouverture et les règles de prêt des documents ? » porte cinq mots significatifs et appelle
deux documents dont chacun ne couvre par construction que sa moitié. Réclamer trois
recoupements les refuse tous les deux, sur une question à laquelle le corpus répond
parfaitement. Un test du dépôt fixe ce bord exact. Le plancher fixe à deux est conservé, et
sa contrepartie assumée : sur une question longue, deux mots suffisent encore à retenir un
extrait qui ne répond pas.

**Additionner les recoupements de l'en-tête et du texte.** Écartée après avoir produit le
pire cas possible : « Quelle est la rémunération d'un agent d'accueil ? » retenait
« Procédure d'accueil au comptoir » parce que « accueil » venait du titre, « agent » du
texte, et « rémunération » de nulle part. L'en-tête dit de quoi le document *parle*, le
texte dit ce que l'extrait *contient* : ce sont deux affirmations distinctes, et il faut
qu'une des deux tienne seule.

**Un vrai petit modèle embarqué** (ONNX, llama.cpp en processus). Écartée pour trois
raisons : un paquet NuGet et des poids à télécharger, donc la fin du « clone et ça marche » ;
un comportement non reproductible d'une machine à l'autre, donc des instantanés
incomparables ; et surtout, la perte de l'argument central — un test qui échoue accuserait
alors le modèle, pas le code.

**Confier le refus au seuil de score plutôt qu'au modèle.** Relever `MinScore` jusqu'à ce
que les questions hors corpus ne rendent plus rien. Écartée parce que cela déplace la
décision hors du modèle : le pipeline refuserait *avant* d'appeler la doublure, et la
démonstration ne montrerait plus rien du comportement d'un modèle face à des extraits
insuffisants. Les deux mécanismes existent d'ailleurs côte à côte, et la trace les
distingue : `NoEvidenceInCorpus` et `NoEvidenceReadableByRequester` sont des refus du
pipeline, `ModelDeclinedToAnswer` est un refus **déclaré** du modèle — à ne pas confondre
avec `ModelProducedEmptyAnswer`, qui signale un modèle muet et non un modèle obéissant.

---

## Références

- `src/AssistantQR.Infrastructure/LanguageModels/ExtractiveLanguageModel.cs` — le `<remarks>` porte la même justification
- `tests/AssistantQR.Infrastructure.Tests/LanguageModels/ExtractiveLanguageModelTests.cs` — dont `CompleteAsync_AucunExtraitNeRecoupeLaQuestion_RenvoieLeMarqueurDeRefus` et `CompleteAsync_UnMotDansLEnteteUnMotDansLeTexte_NeSuffitPas`
- `src/AssistantQR.Application/Model/ModelResponseParser.cs` — le marqueur de refus et sa traduction en `DraftAnswer.DeclinedByModel`
- `src/AssistantQR.Domain/Policies/AnswerPolicy.cs` — la garantie, qui ne dépend d'aucune doublure
- `docs/adr/0006-refus-delibere-et-sortie-vide.md` — pourquoi ce refus a son propre motif
- README racine, section 11 (limites assumées) et `docs/adr/0004-prompts-versionnes.md`
