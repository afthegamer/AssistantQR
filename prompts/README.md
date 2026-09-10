# `prompts/` — les gabarits de prompts, versionnés

Ce dossier contient les instructions envoyées au modèle de langue. Elles ne sont pas
dans le code C# : ce sont des fichiers, nommés, versionnés, et empreintés.

## Convention de nommage

    <nom>@<version>.md

- `<nom>` identifie la **fonction** du gabarit (`answer-with-citations`), pas son contenu.
- `<version>` est un numéro sémantique `major.minor.patch`, comparé par `Version.Parse`
  côté `FileSystemPromptCatalog`. `1.10.0` est donc postérieur à `1.9.0`, contrairement
  à ce que donnerait un tri alphabétique.
- Un fichier = une version **immuable**. On ne corrige pas un gabarit publié : on en
  publie un nouveau. Sinon l'empreinte enregistrée dans un instantané ne désigne plus rien.
- Seuls les fichiers contenant un `@` sont chargés par le catalogue. Ce `README.md` est
  donc ignoré, ce qui permet de documenter le dossier sans polluer le catalogue.

Fichiers présents :

| Fichier | Rôle |
|---|---|
| `answer-with-citations@1.0.0.md` | Gabarit de référence : réponse concise, citations en ligne obligatoires. |
| `answer-with-citations@1.1.0.md` | Variante stricte : une citation par affirmation, aucune introduction, plafond de longueur. |
| `answer-with-citations@1.2.0.md` | Durcissement du 1.0.0 pour les petits modèles locaux : sortie d'une seule ligne, réponse OU marqueur de refus. |
| `refusal-explanation@1.0.0.md` | Rend un refus lisible par un humain, sans y glisser de réponse. |

## En-tête YAML obligatoire

```markdown
---
name: answer-with-citations
version: 1.0.0
description: Répond en français en citant obligatoirement ses sources.
placeholders: [question, evidence, refusal_marker]
---
Corps du prompt…
```

`name` et `version` doivent correspondre au nom du fichier — c'est une redondance
volontaire : elle permet de détecter un fichier renommé à la main.

`placeholders` liste les clés que le corps attend. `PromptTemplate.Render` remplace
chaque double-accolade par sa valeur, refuse de rendre si une clé déclarée manque, et
refuse également s'il subsiste un placeholder non résolu après remplacement. Un gabarit
ne peut donc pas partir vers le modèle à moitié rempli.

Les trois clés utilisées ici :

- `question` — la question de l'usager, telle quelle ;
- `evidence` — le bloc d'extraits produit par `EvidenceFormatter`, sous la forme
  `[identifiant] Titre du document (niveau)` puis le texte, blocs séparés par une ligne
  vide ;
- `refusal_marker` — le marqueur de refus (`ModelResponseParser.RefusalMarker`, soit
  `AUCUNE_REPONSE`). Il est injecté et non écrit en dur : le jour où on le change, on le
  change à un seul endroit, et le parseur comme les gabarits restent d'accord.

## Pourquoi un fichier plutôt qu'une chaîne dans le code

Un prompt a toutes les propriétés d'une donnée de configuration critique, et aucune de
celles d'une constante anodine.

1. **Il change le comportement observable sans changer une ligne de logique.** Une
   virgule déplacée dans le corps peut faire disparaître une citation sur cent réponses.
   Une chaîne noyée dans une méthode ne signale rien de tout cela au relecteur.
2. **Il doit être empreinté.** `PromptTemplate.Fingerprint` est le SHA-256 du corps,
   tronqué à douze caractères hexadécimaux. Cette empreinte voyage dans
   `ConfigurationFingerprint`, donc dans chaque instantané. Une réponse enregistrée sait
   ainsi avec quel texte exact elle a été produite — y compris si quelqu'un a modifié un
   espace en fin de ligne.
3. **Il doit être diffable et relisible.** Un gabarit est un texte qu'un bibliothécaire —
   pas un développeur — doit pouvoir lire, critiquer et amender en revue.
4. **Il doit pouvoir varier sans recompilation.** Comparer 1.0.0 et 1.1.0 se fait en
   changeant `Pipeline.PromptVersion` dans la configuration. Le binaire est identique ;
   c'est précisément ce qui rend la comparaison honnête.
5. **Il est éditorial autant que technique.** Le corps encode des choix de ton, de
   longueur, de politesse. Ce n'est pas de la logique, c'est une politique de rédaction.

Corollaire : le corps du gabarit est chargé depuis le disque, jamais concaténé à la
volée. Toute construction de prompt par morceaux dans le code réintroduit ce qu'on
essaie d'éviter — un prompt qui n'existe nulle part sous forme lisible.

## Ce qu'implique un changement de version

Changer de version de prompt, c'est changer le comportement de **tout** le système. Les
invariantes du domaine tiennent toujours (une réponse sans citation reste impossible),
mais tout le reste bouge : formulation, longueur, nombre d'identifiants cités, et jusqu'à
la frontière entre « je réponds » et « je refuse ».

La procédure est donc :

1. Enregistrer un instantané avec la version en place (`snapshot record`, nom
   `avant-1.1.0`).
2. Changer `Pipeline.PromptVersion`. Ne rien changer d'autre — ni le modèle d'embeddings,
   ni la stratégie de découpage, ni `TopK`. Une seule variable à la fois, sinon la
   comparaison ne prouve rien.
3. Réenregistrer sous `apres-1.1.0`.
4. Comparer. `SnapshotComparison.DriftRatio` donne la proportion de questions dont la
   réponse a bougé, et `ConfigurationDifferences` atteste qu'une seule variable a changé.

Ce que la comparaison 1.0.0 → 1.1.0 doit rendre visible, par construction :

| | 1.0.0 | 1.1.0 |
|---|---|---|
| Phrase d'introduction | tolérée | interdite |
| Longueur | 5 phrases | 3 phrases, 60 mots |
| Citations | au moins une, groupements possibles | une par affirmation, une par ligne |
| Affirmation non citable | peut survivre dans le texte | doit être supprimée |
| Seuil de refus | extraits insuffisants | aucune affirmation citable |

Attendu : `AnswerTextChanged` presque partout, `CitationsChanged` là où 1.1.0 supprime
une affirmation invérifiable, et quelques `RefusalChanged` sur les questions limites —
1.1.0 refuse plus tôt. Un `DriftRatio` proche de zéro après un tel changement n'est pas
une bonne nouvelle : il signifie que le modèle ignore largement les instructions, ou que
le jeu de questions ne discrimine rien.

Enfin, un instantané enregistré avec `answer-with-citations@1.0.0` ne se compare pas à un
instantané pris avec un autre modèle d'embeddings : deux variables auraient bougé.
`ConfigurationFingerprint.DifferencesWith` est là pour le rappeler avant de conclure.

## Pourquoi une 1.2.0 plutôt qu'une correction du 1.0.0

Le 1.0.0 a un défaut mesurable sur un petit modèle local. Avec `granite4.2:3b`, la question
« Quels sont les horaires d'ouverture le samedi ? » produit une réponse juste et citée,
puis un radotage, puis `AUCUNE_REPONSE`. La règle 6 demande d'écrire le marqueur « seul,
sur une ligne » quand les extraits ne suffisent pas ; un modèle de trois milliards de
paramètres l'ajoute par prudence *après* avoir répondu. Ce qui manque au 1.0.0 n'est pas
une règle de plus, c'est l'exclusion mutuelle : une réponse OU le marqueur, jamais les
deux. Le 1.1.0 a le même défaut.

La tentation est d'aller corriger le 1.0.0 sur place — trois lignes suffiraient. C'est
précisément ce que la convention de ce dossier interdit, et pour une raison qui n'est pas
formelle. `PromptTemplate.Fingerprint` est le SHA-256 du corps ; cette empreinte est
enregistrée dans chaque instantané via `ConfigurationFingerprint`. Un instantané pris hier
affirme donc : « ces réponses ont été produites avec `answer-with-citations@1.0.0`, dont le
corps a telle empreinte ». Modifier le fichier sans changer son numéro fait mentir cette
affirmation rétroactivement : l'empreinte enregistrée ne correspond plus à aucun texte
existant, et l'instantané devient invérifiable. Il ne devient pas faux — il devient
inutilisable, ce qui est pire, parce que rien ne le signale. Publier une 1.2.0 laisse le
1.0.0 intact, donc laisse les anciens instantanés comparables entre eux.

Corollaire pratique : le 1.0.0 garde son défaut. C'est voulu. Il reste la ligne de base
contre laquelle le 1.2.0 se mesure, et le dépôt enseigne mieux avec un gabarit fautif
conservé qu'avec un gabarit corrigé en douce.

## Ce que le passage 1.0.0 → 1.2.0 change dans l'empreinte

Rien d'autre que le prompt. Le nom du gabarit ne bouge pas, les trois placeholders
(`question`, `evidence`, `refusal_marker`) sont identiques, le parseur, le formateur
d'extraits et le marqueur de refus sont inchangés. Dans l'empreinte de configuration, deux
champs bougent et deux seulement : la version du gabarit, et l'empreinte de son corps.
Le modèle d'embeddings, la dimension, la stratégie de découpage, `TopK` et `MinScore`
restent où ils étaient — c'est la condition pour que la comparaison prouve quelque chose.

Un instantané pris sous 1.0.0 se compare donc légitimement à un instantané pris sous
1.2.0 : `ConfigurationFingerprint.DifferencesWith` atteste qu'une seule variable a changé,
et `SnapshotComparison.DriftRatio` mesure ce que cette variable a coûté ou rapporté. Ce
qu'on doit voir bouger : `AnswerTextChanged` presque partout, puisque le 1.2.0 impose une
ligne unique là où le 1.0.0 tolérait cinq phrases. `CitationsChanged` là où le 1.0.0
fabriquait des citations parasites en recopiant un en-tête d'extrait. `RefusalChanged`
dans les deux sens, et c'est le point intéressant : le 1.2.0 supprime des refus mal formés
que le 1.0.0 produisait en ajoutant le marqueur à une bonne réponse.

Ce qui ne doit PAS bouger : les questions hors corpus. Sous 1.0.0 comme sous 1.2.0, elles
rendent `ModelDeclinedToAnswer`. Un instantané qui montrerait le 1.2.0 répondant là où le
1.0.0 refusait signalerait une régression du garde-fou, pas un progrès de rédaction.

Enfin, une réserve honnête, parce qu'elle appartient au sujet : le 1.2.0 est calibré sur
un modèle de trois milliards de paramètres. Sur `llama3.2:1b`, il ne suffit pas — le
modèle répond « Samedi. » sans citer, et la politique du domaine rejette la réponse
(`ModelProducedNoCitation`). Le système reste correct, il ne répond simplement plus. C'est
l'argument de la thèse B, plus bas, sous sa forme empirique : un gabarit est calibré pour
un exécutant, et la calibration ne se transporte pas d'un modèle à l'autre.

## Question ouverte : ce dossier appartient-il au domaine ou à l'infrastructure ?

Le corps de `answer-with-citations` contient cette phrase : « Cite tes sources en ligne,
entre crochets, en reprenant l'identifiant EXACT ». C'est mot pour mot la règle métier
n°1, celle que `Answer` rend impossible à violer. La même règle est donc écrite à deux
endroits, dans deux langages, et dans deux couches. Où doit-elle vivre ?

**Thèse A — le gabarit appartient au domaine.**
La règle « une réponse cite ses sources » ne vient pas d'un choix technique : elle vient
de la médiathèque, qui refuse d'engager sa parole sans référence vérifiable. Elle
survivrait à un remplacement complet de la pile technique. Or ce qui survit au changement
de technologie est, par définition, du domaine. Le gabarit n'est pas « du texte pour un
LLM » : c'est la formulation de l'obligation métier à destination d'un exécutant
particulier — un exécutant non déterministe, mais un exécutant. Le placer en
infrastructure revient à laisser une règle métier être modifiée par quelqu'un qui croit
n'ajuster qu'un paramètre de performance. Et le fait que le modèle n'obéisse
qu'imparfaitement ne change rien : un règlement intérieur n'est pas moins du domaine
parce que les agents s'en écartent parfois.

**Thèse B — le gabarit appartient à l'infrastructure.**
Le domaine ne doit connaître aucun mécanisme d'exécution. Or ce texte est saturé de
mécanisme : il parle de crochets, de marqueur de refus, de plafond de longueur, de
formatage ligne à ligne. Il est calibré pour *ce* modèle-ci — un autre modèle demanderait
une autre formulation pour obtenir le même résultat métier. Rien de tout cela n'est
stable. La preuve : la règle métier tient déjà entièrement dans `AnswerPolicy` et
`Answer`, qui n'ont jamais lu un prompt de leur vie. Le gabarit n'est pas la règle, c'est
une **tentative de persuasion** d'un composant externe pour qu'il produise une entrée
acceptable. Une tentative faillible, que le domaine vérifie ensuite sans lui faire
confiance. Ce statut — faillible, spécifique à un fournisseur, remplaçable — est la
définition même d'un détail d'infrastructure.

**Ce que chaque camp doit assumer.**
La thèse A doit expliquer comment un domaine sans dépendance héberge un texte dont la
forme dépend d'un fournisseur précis, et pourquoi il faudrait reversionner le domaine en
passant d'un modèle à un autre. La thèse B doit expliquer comment on empêche une règle
métier de disparaître silencieusement d'un fichier de configuration que personne ne
relit — et pourquoi la duplication entre le prompt et la politique n'est pas une dette,
alors que les deux dérivent séparément.

Le dépôt tranche par la pratique, pas par la théorie : les fichiers sont ici, chargés par
`FileSystemPromptCatalog` (infrastructure), derrière le port `IPromptCatalog`
(application), et l'empreinte du corps est enregistrée dans chaque instantané. Autrement
dit : on traite le gabarit comme de l'infrastructure, mais on le surveille comme du
domaine. C'est un compromis, pas une réponse — et il vaut ce que vaut la discipline de
celui qui relit les instantanés.
