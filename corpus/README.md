# Corpus d'exemple — médiathèque municipale des Tilleuls

Ce dossier contient les 23 documents sur lesquels l'assistant répond. La médiathèque
des Tilleuls, son personnel, ses tarifs et ses chiffres sont **entièrement fictifs**.
Aucun document ne contient d'information réelle sur une personne ou un organisme
existant : les pièces marquées `confidentiel` sont anodines, et ne sont là que pour
donner au contrôle d'accès quelque chose à protéger.

## Format d'un document

Un document est un fichier Markdown avec un en-tête YAML minimal, suivi d'une ligne
vide, puis du texte :

```markdown
---
id: horaires-ouverture
titre: Horaires d'ouverture au public
niveau: public
tags: [accueil, horaires]
---

Premier paragraphe.

Deuxième paragraphe.
```

Quatre clés, toutes obligatoires, dans cet ordre : `id`, `titre`, `niveau`, `tags`.
L'en-tête est lu par `FrontMatterParser` (Infrastructure) ; un fichier mal formé fait
échouer le chargement du corpus avec le nom du fichier dans le message d'erreur, plutôt
que de produire silencieusement un document amputé.

Le corps fait de 3 à 6 paragraphes courts séparés par une ligne vide, pour un total de
150 à 350 mots. Ce n'est pas une coquetterie : `ParagraphChunkingStrategy` découpe sur
les lignes vides. La forme des paragraphes **est** la forme des chunks, donc la forme
des extraits cités.

## Convention d'identifiant

`id` est identique au nom du fichier sans son extension : `horaires-ouverture.md`
porte l'id `horaires-ouverture`. En minuscules, sans accent, mots séparés par des
tirets, sans espace — `DocumentId.From` rejette les espaces, et l'expression régulière
du parseur de réponse (`\[([A-Za-z0-9][A-Za-z0-9_\-]{0,127})\]`) ne reconnaît que ce
jeu de caractères.

Cet identifiant est ce que le modèle écrit entre crochets dans sa réponse, ce que
`AnswerPolicy` vérifie, et ce que l'utilisateur lit à l'écran. Un id lisible n'est donc
pas un détail de présentation : c'est le seul moyen qu'a un lecteur de vérifier une
citation sans quitter la console. Le renommer casse les instantanés enregistrés.

## Niveaux d'accès

`niveau` est écrit **en français** dans le corpus (`public`, `interne`, `confidentiel`)
et traduit vers `AccessLevel` par l'Infrastructure. Le Domain, lui, ne connaît que
`public` / `internal` / `confidential`. La frontière est volontaire : la langue du
corpus est une caractéristique du support de stockage, pas une règle métier.

| niveau | rang | documents | qui peut lire |
|---|---|---|---|
| `public` | 0 | 11 | tout le monde |
| `interne` | 1 | 8 | agents de la médiathèque |
| `confidentiel` | 2 | 4 | direction et ressources humaines |

Un lecteur voit tout ce dont le rang est inférieur ou égal à son habilitation
(`AccessLevel.IsReadableWith`). Il n'y a pas de compartimentage : l'habilitation est
un ordre total, pas un ensemble de droits.

## Pourquoi le vocabulaire se recoupe volontairement entre les niveaux

C'est la propriété la plus importante de ce corpus, et la plus contre-intuitive.

Un corpus « bien rangé », où chaque niveau parlerait de sujets distincts, rendrait tout
le cours invisible. Si les documents confidentiels traitaient de sujets que personne ne
demande, la recherche vectorielle ne les remonterait jamais, et **pré-filtrer ou
post-filtrer donnerait exactement le même résultat**. La démonstration centrale —
montrer que la place du filtre change la réponse — tomberait à plat, et l'on en
conclurait à tort que le choix n'a pas d'importance.

Trois familles de documents partagent donc délibérément leur vocabulaire à travers les
niveaux :

- **Le retard.** `retards-amendes` (public), `gestion-retards-interne` (interne) et
  `contentieux-usagers` (confidentiel) parlent tous de retard, de relance, de document
  non rendu, d'usager et de suspension du droit de prêt. Une question sur les retards
  remonte donc des fragments des trois niveaux. En post-filtrage, les fragments
  confidentiels consomment des places du `topK` avant d'être retirés : il reste moins
  de matière qu'en pré-filtrage, pour la même question et la même habilitation.
- **L'agent.** `grille-remuneration` (confidentiel), `planning-agents` (interne) et
  `procedure-accueil` (interne) parlent tous d'agent, de poste, de service, d'horaire
  et d'indemnité. Les deux documents internes mentionnent l'existence de l'indemnité
  de service public *sans son barème*, qui n'existe que dans le document confidentiel.
  C'est le meilleur test de non-hallucination du corpus : le modèle reçoit des extraits
  pertinents mais insuffisants, et doit refuser plutôt que compléter.
- **La collection.** `desherbage-collections`, `inventaire-annuel` et
  `budget-acquisitions` parlent tous de collection, d'acquisition, d'exemplaire et de
  budget. Ces trois documents sont au même niveau : ils servent à montrer qu'un `topK`
  trop petit fait perdre un fait pourtant lisible, indépendamment de tout contrôle
  d'accès.

Deuxième propriété, du même ordre : chaque document contient des **faits précis** —
montants en euros, horaires, durées, pourcentages, effectifs. Un corpus de généralités
produirait des réponses vagues, qu'aucune citation ne permettrait de contredire et dont
aucune dérive ne serait mesurable d'un instantané à l'autre. Ce sont les chiffres qui
rendent une réponse fausse *visiblement* fausse.

## Pourquoi chaque document ouvre sur un paragraphe dense

Chaque document commence par un paragraphe court — 25 à 40 mots — qui énonce son objet
avec les mots que quelqu'un emploierait pour poser la question, et qui donne tout de
suite le fait principal. Ce n'est pas un tic de rédaction : c'est une contrainte imposée
par le modèle d'embeddings hors-ligne.

`HashingEmbeddingService`, le modèle par défaut du profil `offline`, projette les mots et
les bigrammes dans autant de cases que la dimension configurée — 1024 dans ce dépôt, 256
si l'on s'en tient au défaut du code — par hachage, puis normalise. Sur des paragraphes de
cinquante mots, les mots outils du français — de, la, les, des, à, un, en — occupent
l'essentiel du vecteur, les collisions sont massives, et deux paragraphes quelconques se
ressemblent autant que deux paragraphes qui parlent du même sujet. Mesuré sur une
première version de ce corpus, la question « Quels sont les horaires d'ouverture le
samedi ? » plaçait `horaires-ouverture` au **27e rang**, derrière `inventaire-annuel` et
`dons-documents`. Après réécriture des seuls premiers paragraphes, sans toucher aux
questions : **1er rang**. Les douze questions de `snapshots/questions.json` retrouvent
aujourd'hui leur document cible dans les quatre premiers résultats.

Deux leçons, et c'est pour elles que le corpus est écrit ainsi :

1. **La structure du document conditionne la qualité de la recherche autant que le
   modèle.** Un chunk long et délayé est un mauvais chunk, quel que soit l'embedding.
2. **Corpus et jeu de questions se conçoivent ensemble.** Ce corpus est accordé à ses
   douze questions ; c'est légitime pour un support de cours, et malhonnête dans une
   évaluation. La différence tient uniquement à ce qu'on prétend mesurer.

Avec un vrai modèle d'embeddings (profil `local`), la sensibilité à cette mise en forme
diminue nettement — sans disparaître. Comparer les deux profils sur les mêmes questions
est l'un des exercices que ce dépôt rend possible.

## Inventaire

**Public (11)** — `horaires-ouverture`, `inscription-abonnement`, `pret-documents`,
`retards-amendes`, `espace-jeunesse`, `acces-wifi-postes`, `salles-de-travail`,
`catalogue-en-ligne`, `accessibilite`, `programme-animations`, `dons-documents`.

**Interne (8)** — `procedure-accueil`, `gestion-retards-interne`,
`desherbage-collections`, `inventaire-annuel`, `ouverture-fermeture`,
`budget-acquisitions`, `planning-agents`, `incident-materiel`.

**Confidentiel (4)** — `grille-remuneration`, `procedure-disciplinaire`,
`contentieux-usagers`, `contrat-maintenance`.

## Modifier le corpus

Ajouter, retirer ou réécrire un document change les chunks, donc les vecteurs, donc les
extraits retenus, donc les réponses. Un instantané enregistré avant la modification
n'est plus comparable terme à terme à un instantané pris après : la
`ConfigurationFingerprint` ne capture pas le contenu du corpus, seulement la
configuration du pipeline. Réindexer (`index`) puis réenregistrer une base de référence
fait partie de l'opération, au même titre que l'édition du fichier.
