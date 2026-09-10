# ADR 0004 — Les prompts sont des artefacts versionnés et empreintés

- **Statut** : acceptée
- **Portée** : `prompts/`, `src/AssistantQR.Application/Model/PromptTemplate.cs`,
  `src/AssistantQR.Application/Ports/IPromptCatalog.cs`,
  `src/AssistantQR.Infrastructure/Prompts/FileSystemPromptCatalog.cs`,
  `src/AssistantQR.Application/UseCases/Snapshots/ConfigurationFingerprint.cs`

---

## Contexte

Le prompt envoyé au modèle de langue détermine le comportement observable du système :
langue de la réponse, longueur, présence des citations, seuil au-delà duquel le modèle
refuse plutôt que d'extrapoler. Une virgule déplacée dans son corps peut faire disparaître
une citation sur cent réponses.

Il a donc toutes les propriétés d'une **donnée de configuration critique** et aucune de
celles d'une constante anodine :

- il change le comportement sans changer une ligne de logique ;
- il se modifie sans recompilation ;
- il est éditorial autant que technique — ton, longueur, politesse — et devrait pouvoir
  être relu par un bibliothécaire, pas seulement par un développeur ;
- il n'est jamais respecté à cent pour cent, parce que son destinataire est probabiliste.

La solution par défaut — une chaîne interpolée dans une méthode du cas d'usage — rend tout
cela invisible. On ne peut ni relire le prompt sans lire le code, ni comparer deux versions
sur le même jeu de questions, ni savoir avec quel texte exact une réponse enregistrée il y
a trois mois a été produite.

Le projet a par ailleurs besoin d'un mécanisme d'imputation : quand les réponses dérivent,
il faut pouvoir dire **ce qui a changé**. Le prompt est l'une des variables candidates, au
même titre que le modèle d'embeddings, la stratégie de découpage et le mode de filtrage.

---

## Décision

**Un prompt est un fichier, nommé, versionné, immuable et empreinté.**

1. **Convention de nommage** : `prompts/<nom>@<version>.md`. `<nom>` identifie la
   *fonction* (`answer-with-citations`), `<version>` est un numéro sémantique
   `major.minor.patch` comparé par `Version.Parse` — `1.10.0` est donc postérieur à
   `1.9.0`, contrairement à un tri alphabétique.
2. **En-tête YAML obligatoire** : `name`, `version`, `description`, `placeholders`. `name`
   et `version` doivent correspondre au nom de fichier ; cette redondance permet de
   détecter un fichier renommé à la main.
3. **Immuabilité** : on ne corrige pas un gabarit publié, on en publie un nouveau. Sinon
   l'empreinte enregistrée dans un instantané ne désigne plus rien.
4. **Empreinte** : `PromptTemplate.Fingerprint` est le SHA-256 du corps, tronqué à douze
   caractères hexadécimaux minuscules. Elle voyage dans `ConfigurationFingerprint`, donc
   dans chaque instantané.
5. **Rendu strict** : `Render` refuse si une clé déclarée manque, et refuse également s'il
   subsiste un `{{…}}` non résolu après remplacement. Un gabarit ne peut pas partir vers le
   modèle à moitié rempli.
6. **Marqueur de refus injecté** : `refusal_marker` est passé en valeur, pas écrit en dur.
   Le jour où on le change, le parseur et les gabarits restent d'accord.
7. **Port synchrone** : `IPromptCatalog` charge tout au démarrage. Un gabarit manquant est
   une erreur de configuration qui doit exploser tôt et bruyamment, pas une latence à gérer
   à chaque question.
8. **Deux versions coexistent dans le dépôt** : `answer-with-citations@1.0.0` (référence) et
   `@1.1.0` (variante stricte : une citation par affirmation, aucune introduction, trois
   phrases, soixante mots). Elles existent pour être comparées, pas pour choisir.

---

## Conséquences

### Positives

**Le prompt devient une variable d'expérience.** Une seule chose change, et la comparaison
l'atteste :

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

Le binaire est identique. C'est ce qui rend la comparaison honnête.

**Une réponse enregistrée sait avec quel texte exact elle a été produite** — y compris si
quelqu'un a modifié un espace en fin de ligne. C'est le seul moyen d'imputer une dérive à
une modification de prompt six semaines plus tard.

**Le gabarit est relisible et amendable en revue.** C'est un fichier Markdown de
cinquante lignes, diffable, commentable, que quelqu'un qui ne lit pas C# peut critiquer.

**La comparaison de deux prompts a un attendu documenté.** `prompts/README.md` donne le
tableau des effets de 1.0.0 → 1.1.0 et la lecture des résultats : `AnswerTextChanged`
presque partout, `CitationsChanged` là où 1.1.0 supprime une affirmation invérifiable,
quelques `RefusalChanged` sur les questions limites. **Un `DriftRatio` proche de zéro après
un tel changement n'est pas une bonne nouvelle** : il signifie que le modèle ignore
largement les instructions, ou que le jeu de questions ne discrimine rien.

### Négatives, assumées

**En profil `offline`, la comparaison de versions donne 0 % de dérive.**
`ExtractiveLanguageModel` ne lit pas les instructions du prompt : il relit le bloc
d'extraits et recopie. Le mécanisme d'empreinte fonctionne parfaitement — la configuration
enregistre bien deux empreintes différentes — mais la mesure est vide. C'est un résultat
instructif (un modèle qui ignore le prompt produit exactement cette signature) et une
limite réelle : la démonstration de cette tension exige Ollama.

**L'immuabilité est une convention, pas une contrainte.** Rien n'empêche d'éditer
`answer-with-citations@1.0.0.md` en place. L'empreinte le révélerait a posteriori, dans la
comparaison d'instantanés — pas au moment de la faute.

**L'empreinte détecte le changement, pas la régression.** Elle dit qu'on a modifié le texte.
Elle ne dit pas que la modification a supprimé l'obligation de citer. Rien dans le dépôt ne
teste que le corps d'un gabarit contient encore son instruction de citation.

**La règle métier n° 1 est écrite à deux endroits.** « Cite tes sources en ligne, entre
crochets » dans le gabarit ; `Answer.Create` qui refuse une liste de citations vide dans le
Domain. Les deux peuvent dériver l'une de l'autre sans que rien ne le signale. Le seul
filet est qu'`AnswerPolicy` refusera — mais un refus systématique après une modification
malheureuse du prompt sera d'abord attribué au modèle.

**Le parseur et le gabarit ne sont pas dans la même couche.** `ModelResponseParser` est en
Application, le gabarit en Infrastructure derrière `IPromptCatalog`, alors qu'ils encodent
le même format et doivent bouger ensemble. C'est incohérent, et le commentaire du parseur
l'admet. C'est le dilemme 9b du README, laissé ouvert.

**Le versionnement sémantique d'un prompt est mal défini.** Qu'est-ce qu'un changement
`major` pour un texte adressé à un modèle ? Le dépôt utilise `1.0.0` / `1.1.0` par
convention, sans règle formelle. C'est un point faible connu.

---

## Alternatives écartées

**Chaîne littérale dans le cas d'usage.**
La solution par défaut. Écartée pour cinq raisons cumulatives : le prompt ne serait plus
relisible sans lire le code, plus empreintable, plus comparable sans recompilation, plus
amendable par un non-développeur, et son changement ne signalerait rien au relecteur d'une
`pull request` qui verrait passer une chaîne parmi d'autres.

**Prompt en base de données, éditable par une interface.**
C'est le bon choix quand des non-développeurs itèrent en continu. Écartée ici : elle ajoute
une base au dépôt, elle rend le prompt non versionné avec le code, et elle supprime la
propriété la plus utile en cours — pouvoir montrer un `git diff` entre 1.0.0 et 1.1.0.

**Construction du prompt par morceaux dans le code** (en-tête + règles + pied assemblés à
la volée, selon des options).
Séduisante parce qu'elle évite la duplication entre 1.0.0 et 1.1.0. Écartée parce qu'elle
réintroduit exactement ce qu'on cherche à éviter : **un prompt qui n'existe nulle part sous
forme lisible**. On ne peut pas empreinter un texte qui n'est assemblé qu'à l'exécution
sans reconstituer mentalement l'assemblage.

**Une seule version de prompt, sans versionnement.**
Écartée parce qu'elle rendrait la quatrième tension du cours indémontrable. Le versionnement
n'est pas là pour gérer une bibliothèque de gabarits — il y en a trois — mais pour rendre
un changement de prompt **attribuable**.

**Prompt dans le Domain.**
Position défendable, exposée intégralement dans `prompts/README.md` et dans la section 9b
du README racine. Écartée en pratique — le corps est saturé de mécanisme lié à un modèle
donné, et le Domain ne doit connaître aucun mécanisme d'exécution — mais la décision est
présentée comme un **compromis, pas une réponse** : le gabarit est traité comme de
l'infrastructure et surveillé comme du domaine.

---

## Références

- `prompts/README.md` — convention complète, effets attendus de 1.0.0 → 1.1.0, la question ouverte
- `src/AssistantQR.Application/Model/PromptTemplate.cs` — empreinte et rendu strict
- `src/AssistantQR.Application/Model/ModelResponseParser.cs` — le pendant du gabarit
- `src/AssistantQR.Application/UseCases/Snapshots/ConfigurationFingerprint.cs` — où l'empreinte voyage
- README racine, sections 6.3 et 9b
