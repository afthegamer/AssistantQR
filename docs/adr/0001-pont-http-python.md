# ADR 0001 — Le pont C# / Python passe par HTTP local

- **Statut** : acceptée
- **Portée** : `src/AssistantQR.Infrastructure/Embeddings/`, `src/AssistantQR.Infrastructure/VectorIndex/`,
  `src/AssistantQR.Infrastructure/Http/`, `python/embeddings_service/`

---

## Contexte

Le calcul des embeddings et l'index vectoriel vivent en Python : c'est là que se trouvent
numpy, le client Ollama et l'écosystème du domaine. L'application, ses règles métier et sa
ligne de commande vivent en .NET 8. Il faut donc un pont entre les deux processus.

Trois contraintes cadrent le choix.

1. **Le dépôt doit rester un support de cours.** Quelqu'un qui clone doit pouvoir faire
   tourner l'application sans Python du tout — c'est le rôle du profil `offline` et de
   `HashingEmbeddingService`. Le pont ne doit donc jamais être sur le chemin critique du
   démarrage.
2. **Les pannes doivent être diagnosticables par un étudiant.** « Ollama n'est pas lancé »,
   « le modèle n'est pas téléchargé », « la dimension ne correspond pas » sont les trois
   erreurs qui arriveront en séance. Chacune doit produire une phrase qui dit quoi taper.
3. **Le contrat doit être lisible sans lire le code.** On veut pouvoir projeter le tableau
   des routes et le comprendre, en C# comme en Python.

L'alternative sérieuse était le **sous-processus** : lancer un script Python, écrire du
JSON sur `stdin`, lire `stdout`. Elle a l'avantage évident de ne demander ni port, ni
serveur, ni gestion du cycle de vie.

---

## Décision

**Le service Python est un serveur HTTP local, écoutant sur `http://localhost:8088`, et le
C# est son client.** Le contrat est six routes JSON en `snake_case`, décrites dans
`python/embeddings_service/README.md` et résumées dans le README racine.

Côté C#, deux adaptateurs distincts consomment le même service —
`HttpEmbeddingService` (`IEmbeddingService`) et `HttpVectorIndex` (`IVectorIndex`) — via
un `HttpClient` nommé `"embeddings"` fourni par `IHttpClientFactory`. Le helper
`Http/JsonHttp.cs` centralise la sérialisation et la lecture du corps d'erreur.

---

## Conséquences

### Positives

**Les erreurs sont typées et actionnables.** Toute réponse non-2xx a la même forme :

```json
{"error": {"code": "MODEL_NOT_FOUND",
           "message": "Ollama ne connait pas le modele d'embeddings « bge-m3 ».",
           "hint": "Lance : ollama pull bge-m3"}}
```

Le C# désérialise et construit une `EmbeddingServiceException` ou une
`VectorIndexException` avec une phrase française exploitable. `Program.Explain` la laisse
passer telle quelle, en ajoutant seulement « Diagnostic complet : assistantqr doctor ».

**Le modèle est chauffé une seule fois.** Le `lifespan` de `app/main.py` envoie un
embedding à vide au démarrage : Ollama charge les poids et les garde en mémoire. Le coût de
chargement — plusieurs secondes — est payé une fois, pas à chaque question.

**Le service se teste seul, sans SDK .NET.** `curl http://localhost:8088/health` répond.
`scripts/smoke-test.sh` parcourt tout le contrat. Quand une démonstration échoue en séance,
la question « est-ce le C# ou le Python ? » se tranche en une commande.

**La frontière est réelle des deux côtés.** Les tests Python de `tests/test_api.py`
tournent **sans Ollama**, parce que le client d'embeddings est injecté dans `create_app`.
Les tests C# hors ligne tournent sans Python, parce que `IEmbeddingService` a un faux.
Chaque couche est isolable indépendamment.

**Le contrat est projetable.** Six lignes de tableau, des noms de champs en `snake_case`,
des niveaux d'accès en anglais. On peut le montrer en cours sans montrer une ligne de code.

### Négatives, assumées

**Un processus de plus à démarrer.** Le cas « service pas encore lancé » existe et doit être
géré : c'est le rôle de `GET /health`, de `EnvironmentProbe` et de `assistantqr doctor`.
Le profil `offline` reste le contournement universel.

**Un port à surveiller.** `8088` peut être occupé. Le réglage est dans
`AssistantQR:Embeddings:ServiceUrl` et se surcharge par variable d'environnement
(`ASSISTANTQR_EMBEDDINGS__SERVICEURL`).

**Une latence réseau, même en local.** Négligeable devant le coût d'un embedding, mais
réelle sur des lots. C'est la raison pour laquelle `IEmbeddingService.EmbedDocumentsAsync`
prend une liste et non une chaîne, et pourquoi `IndexCorpusUseCase` envoie par lots de 32.

**Deux adaptateurs C# pour un seul service Python.** `HttpEmbeddingService` et
`HttpVectorIndex` partagent le même `HttpClient` nommé mais implémentent deux ports
différents. C'est voulu : la frontière suit le rôle, pas le déploiement. Le jour où l'index
part chez un fournisseur tiers, seul `HttpVectorIndex` change.

**Conséquence de composition.** La racine de composition fait basculer embeddings et index
**d'un seul bloc**. Garder l'index distant tout en calculant les vecteurs avec le faux
enverrait des vecteurs hachés à un index construit par un vrai modèle : l'index répondrait
sans broncher, avec des scores parfaitement plausibles. C'est précisément la panne du
scénario B, et il n'y a aucune raison de la rendre atteignable par distraction de
configuration.

---

## Alternatives écartées

**Sous-processus avec JSON sur `stdin`/`stdout`.**
Séduisante par sa simplicité de déploiement. Écartée pour trois raisons cumulatives. La
première est le diagnostic : une panne devient une trace Python noyée dans `stderr`,
mélangée aux avertissements de numpy et aux messages de chargement de modèle, à découper à
coups d'expressions régulières — et le code de sortie vaut `1` dans tous les cas. La
deuxième est le coût de démarrage : un processus relancé à chaque appel recharge les poids
du modèle à chaque question. La troisième est la testabilité : on ne teste pas un contrat
`stdin`/`stdout` avec `curl`.

**Portage du calcul d'embeddings en C#** (ONNX Runtime, tokenizers gérés).
Supprime le pont entièrement. Écartée parce qu'elle déplace le sujet du cours : le
tokenizer, la quantification et la gestion des sessions ONNX occuperaient plus de place que
les frontières, qui sont le propos. Et elle ne supprime pas le problème pédagogique, elle le
cache : on veut justement montrer un système à deux runtimes.

**Appel direct à Ollama depuis le C#, sans service Python.**
Techniquement possible pour `/api/embed`. Écartée parce qu'il faudrait alors écrire l'index
vectoriel en C# aussi, et qu'on perdrait la démonstration du service Python qui **connaît
le contrôle d'accès** (`max_access_level` dans `/index/search`) — laquelle est la
contrepartie concrète du dilemme du filtrage. Noter que le C# **parle quand même**
directement à Ollama pour la génération (`OllamaLanguageModel`) : le service Python n'est
pas un proxy universel, il porte les embeddings et l'index, rien d'autre.

**gRPC.**
Contrat typé, génération de code, meilleures performances sur les gros lots. Écartée pour
le cours : on perd `curl`, on perd la lisibilité du contrat à l'œil nu, et on ajoute une
chaîne d'outils (`protoc`, plugins) à installer avant de voir la première réponse.

**Base vectorielle dédiée** (Qdrant, Weaviate, pgvector).
Écartée par la contrainte du dépôt : aucune base, aucun conteneur obligatoire. Et une base
tierce masquerait l'objet de la démonstration — un index qui accepte sans broncher des
vecteurs d'un autre modèle est plus parlant quand on peut lire les quarante lignes qui le
font.

---

## Références

- `python/embeddings_service/README.md` — contrat complet, codes d'erreur, panne silencieuse
- `src/AssistantQR.Infrastructure/Http/JsonHttp.cs` — sérialisation et lecture des erreurs
- `src/AssistantQR.Infrastructure/Diagnostics/EnvironmentProbe.cs` — sondes de `doctor`
- `docker-compose.yml`, `scripts/start-embeddings.*`, `scripts/smoke-test.*`
