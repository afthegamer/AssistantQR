# Service d'embeddings et index vectoriel

Petit service HTTP qui rend deux services au reste d'AssistantQR :

1. transformer du texte en vecteurs, en deleguant le calcul a **Ollama** ;
2. tenir un **index vectoriel** (numpy, similarite cosinus, persistance disque)
   et repondre aux recherches par plus proches voisins.

Il ne connait ni le corpus, ni les prompts, ni les regles de citation. Il ne
contient aucun framework RAG : pas de LangChain, pas de LlamaIndex, pas de
sentence-transformers. Les deux fichiers a lire pour comprendre le fond sont
`app/index.py` (pre-filtrage, panne silencieuse) et `app/config.py` (principe
CACE).

---

## Pourquoi un service HTTP et pas un sous-processus

L'alternative naturelle etait de lancer un script Python en sous-processus
depuis le C#, de lui passer du JSON sur `stdin` et de lire `stdout`. Trois
raisons ont fait pencher la balance :

**1. Des erreurs lisibles.** Ici, une panne arrive avec un code de statut HTTP
et un corps structure :

```json
{"error": {"code": "MODEL_NOT_FOUND", "message": "Ollama ne connait pas le modele d'embeddings « bge-m3 ».", "hint": "Lance : ollama pull bge-m3"}}
```

Le C# deserialise cet objet et construit une `EmbeddingServiceException` qui
dit quoi taper pour s'en sortir. En sous-processus, la meme panne serait une
trace Python noyee dans `stderr`, melangee aux avertissements de numpy et aux
messages de chargement de modele, a decouper a coups d'expressions regulieres.
Le code de sortie, lui, vaudrait `1` dans tous les cas.

**2. Le modele est chauffe une seule fois.** Au demarrage, le `lifespan` de
`app/main.py` envoie un embedding a vide : Ollama charge les poids en memoire
et les y garde. Toutes les questions suivantes tapent dans un modele deja
chaud. Un sous-processus relance a chaque appel paierait ce chargement a chaque
question — plusieurs secondes, pour un travail qui en demande quelques
centaines de millisecondes.

**3. Le service se teste seul.** `curl http://localhost:8088/health` suffit a
savoir si le probleme vient du Python ou du C#, sans SDK .NET installe. Le
script `scripts/smoke-test.sh` parcourt tout le contrat de cette facon. La meme
frontiere permet aux tests de `tests/test_api.py` de tourner **sans Ollama** :
le client est injecte, donc substituable.

Le prix a payer est honnete : un processus de plus a demarrer, un port a
surveiller, et la necessite de gerer le cas « service pas encore lance ». C'est
precisement ce que `docker compose up` et `GET /health` couvrent.

---

## Lancer le service

### En une commande, avec Docker

Depuis la racine du depot :

```bash
docker compose up --build
```

Le service ecoute sur `http://localhost:8088` et parle a l'Ollama de la machine
hote (`host.docker.internal`). Si Ollama n'est pas installe sur la machine,
utilise le profil optionnel :

```bash
OLLAMA_URL=http://ollama:11434 docker compose --profile ollama up --build
docker compose --profile ollama exec ollama ollama pull qwen3-embedding:0.6b
```

### En environnement virtuel

```bash
./scripts/start-embeddings.sh          # Linux, macOS, Git Bash
```

```powershell
.\scripts\start-embeddings.ps1         # Windows
```

Ces scripts creent le venv dans `python/embeddings_service/.venv`, installent
les dependances epinglees de `requirements.txt`, puis lancent
`uvicorn app.main:app --port 8088`.

### Modeles Ollama requis

```bash
./scripts/pull-models.sh               # granite4.2:3b, qwen3-embedding:0.6b, bge-m3
```

### Verifier que tout repond

```bash
./scripts/smoke-test.sh                # lecture seule
./scripts/smoke-test.sh --write        # ajoute reset/upsert : DETRUIT l'index en place
```

---

## Configuration

Toutes les variables sont documentees dans `.env.example`. Les trois qui
comptent :

| Variable | Defaut | Role |
|---|---|---|
| `EMBEDDING_MODEL` | `qwen3-embedding:0.6b` | `qwen3-embedding:0.6b` ou `bge-m3`. Les deux font 1024 dimensions. |
| `OLLAMA_URL` | `http://localhost:11434` | Ou joindre Ollama. |
| `DATA_DIR` | `python/embeddings_service/data` | Ou l'index est persiste (`index.npy` + `index.json`). |

**Changer `EMBEDDING_MODEL` oblige a reindexer le corpus.** Rien dans le
service ne t'y forcera : voir « la panne silencieuse » plus bas.

### Les deux modeles ne sont pas interchangeables pour rien

`qwen3-embedding:0.6b` a ete entraine avec une consigne en tete des requetes
(`Instruct: ...\nQuery: ...`) et en tire un gain reel ; `bge-m3` n'en a pas et
se degrade si on lui en colle une. Le service applique donc un prefixe
d'instruction aux appels `kind: "query"` **uniquement quand le modele courant
en attend un** (voir `EmbeddingModelSpec` dans `app/config.py`). Les documents
ne sont jamais prefixes, sinon l'index et les requetes ne vivraient plus dans
le meme espace.

C'est l'illustration la plus courte du principe **CACE** (*Changing Anything
Changes Everything*) : une variable d'environnement qui ne modifie aucune
signature change quand meme les vecteurs, donc les documents retrouves, donc la
reponse finale.

---

## Contrat HTTP

Base : `http://localhost:8088`. Corps JSON en UTF-8, noms de champs en
`snake_case` (le client C# deserialise avec `JsonNamingPolicy.SnakeCaseLower`).
Le niveau d'acces circule **en anglais** : `public`, `internal`,
`confidential` — la traduction depuis le francais du corpus est faite cote C#.

### `GET /health`

Repond toujours `200`, meme quand Ollama est injoignable : une sonde qui tombe
en meme temps que ce qu'elle surveille ne sert a rien. La verite est dans
`ollama_reachable`.

```bash
curl -s http://localhost:8088/health
```

```json
{
  "status": "ok",
  "embedding_model": "qwen3-embedding:0.6b",
  "dimension": 1024,
  "ollama_reachable": true,
  "index_chunk_count": 0
}
```

### `POST /embed`

`kind` vaut `"query"` ou `"document"` (defaut : `document`).

```bash
curl -s -X POST http://localhost:8088/embed \
  -H 'Content-Type: application/json' \
  -d '{"texts":["Quels sont les horaires d'"'"'ouverture le samedi ?"],"kind":"query"}'
```

```json
{"model": "qwen3-embedding:0.6b", "dimension": 1024, "vectors": [[0.0123, -0.0456, "..."]]}
```

### `POST /index/reset`

Vide l'index et enregistre qui l'a construit. A appeler avant toute serie
d'`upsert`.

```bash
curl -s -X POST http://localhost:8088/index/reset \
  -H 'Content-Type: application/json' \
  -d '{"model":"qwen3-embedding:0.6b","dimension":1024,"chunking_strategy_id":"paragraph"}'
```

```json
{"ok": true}
```

### `POST /index/upsert`

Les elements sont apparies par `chunk_id` : reenvoyer le meme identifiant
remplace l'entree au lieu de la dupliquer. `count` est le nombre d'elements
ecrits **par cet appel**, pas la taille finale de l'index.

```bash
curl -s -X POST http://localhost:8088/index/upsert \
  -H 'Content-Type: application/json' \
  -d '{"items":[{"chunk_id":"horaires-ouverture#0","document_id":"horaires-ouverture","document_title":"Horaires d'"'"'ouverture au public","access_level":"public","ordinal":0,"text":"La mediatheque ouvre le samedi de 10h a 18h.","vector":[0.1,0.2]}]}'
```

```json
{"count": 1}
```

### `POST /index/search`

`max_access_level` est optionnel. **Quand il est fourni, le filtrage a lieu
avant le top-k** (pre-filtrage) ; quand il vaut `null`, le service rend les k
meilleurs tous niveaux confondus et c'est au client de filtrer (post-filtrage).
Les deux strategies existent pour qu'on puisse comparer leurs resultats sur la
meme question.

```bash
curl -s -X POST http://localhost:8088/index/search \
  -H 'Content-Type: application/json' \
  -d '{"vector":[0.1,0.2],"top_k":4,"max_access_level":"internal"}'
```

```json
{
  "results": [
    {
      "chunk_id": "gestion-retards-interne#2",
      "document_id": "gestion-retards-interne",
      "document_title": "Traitement interne des retards",
      "access_level": "internal",
      "ordinal": 2,
      "text": "Apres la troisieme relance, le dossier passe au service...",
      "score": 0.8123
    }
  ]
}
```

### `GET /index/metadata`

```bash
curl -s http://localhost:8088/index/metadata
```

```json
{
  "embedding_model": "qwen3-embedding:0.6b",
  "dimension": 1024,
  "chunking_strategy_id": "paragraph",
  "chunk_count": 123,
  "built_at": "2026-01-01T10:00:00+00:00"
}
```

`built_at` est `null` tant que l'index n'a jamais ete construit.
`embedding_model` est le modele qui a construit l'index, **pas** celui que le
service utilise aujourd'hui. Les deux peuvent differer.

> Sous PowerShell, `curl` est un alias d'`Invoke-WebRequest` et n'accepte pas
> ces options. Utilise `curl.exe` avec les commandes ci-dessus, ou
> `Invoke-RestMethod` (voir `scripts/smoke-test.ps1`).

---

## La panne silencieuse, en clair

`POST /index/search` **ne refuse jamais** une requete au motif que le vecteur
vient d'un autre modele que celui qui a construit l'index, tant que la dimension
correspond. Comme `qwen3-embedding:0.6b` et `bge-m3` produisent tous deux des
vecteurs de dimension 1024, un index construit par l'un s'interroge sans un mot
avec des vecteurs de l'autre : le produit scalaire se calcule, les scores
tombent entre 0 et 1, les resultats ont l'air normaux. Ils sont faux.

Ce comportement est voulu. Le service expose la matiere premiere du diagnostic
(`GET /index/metadata`) et laisse au client la responsabilite de comparer —
c'est ce que fait `AnswerQuestionUseCase` cote C#, qui produit un
`IndexModelWarning` (et peut lever si `StrictIndexModelCheck` est actif). La
lecon du cours tient dans le fait qu'aucun exception, aucun journal, aucun code
de statut ne signale cette panne : seule la pertinence s'effondre.

Reproduis-la en trois commandes :

```bash
EMBEDDING_MODEL=qwen3-embedding:0.6b ./scripts/start-embeddings.sh   # indexe le corpus
EMBEDDING_MODEL=bge-m3 ./scripts/start-embeddings.sh                 # relance sans reindexer
./scripts/smoke-test.sh                                              # le script signale le desaccord
```

Le seul refus legitime est `DIMENSION_MISMATCH` : quand les dimensions
different, le produit scalaire n'est meme pas calculable.

---

## Codes d'erreur

Toutes les reponses non-2xx ont la meme forme :

```json
{"error": {"code": "...", "message": "...", "hint": "..."}}
```

| Code | Statut | Quand |
|---|---|---|
| `EMPTY_INPUT` | 400 | `/embed` sans texte ou avec un texte blanc ; `/index/search` avec un vecteur vide. |
| `DIMENSION_MISMATCH` | 400 | Dimension annoncee, calculee ou indexee incoherente ; vecteur de requete de mauvaise taille. |
| `INVALID_ACCESS_LEVEL` | 400 | `access_level` ou `max_access_level` hors de `public`/`internal`/`confidential`. |
| `INVALID_REQUEST` | 400 | JSON malforme ou champ du mauvais type (seul code hors contrat, ajoute pour que FastAPI ne renvoie pas son 422 maison). |
| `MODEL_NOT_FOUND` | 404 | Ollama ne connait pas le modele demande. Le `hint` donne la commande `ollama pull`. |
| `INDEX_EMPTY` | 409 | `/index/search` sur un index vide, ou `/index/upsert` avant tout `/index/reset`. |
| `OLLAMA_UNREACHABLE` | 503 | Ollama ne repond pas a l'adresse configuree. |
| `INTERNAL_ERROR` | 500 / 502 | Reponse inattendue d'Ollama, ou bogue du service. |

---

## Tests

Ils tournent **sans Ollama** : le client d'embeddings est injecte dans
`create_app`, donc remplace par une doublure deterministe.

```bash
python -m venv .venv
.venv/bin/pip install -r requirements-dev.txt      # .venv\Scripts\pip.exe sous Windows
.venv/bin/python -m pytest tests -q
```

Les tests couvrent le contrat de bout en bout : forme des reponses, chacun des
codes d'erreur, application (et non-application) de la consigne d'instruction,
pre-filtrage avant le top-k, persistance de l'index entre deux demarrages, et
la panne silencieuse elle-meme — qui est verifiee comme un comportement
**attendu**, pas comme un bogue.

---

## Organisation du code

| Fichier | Role |
|---|---|
| `app/config.py` | Variables d'environnement, catalogue des modeles, consignes d'instruction (CACE). |
| `app/models.py` | Schemas pydantic entrants et sortants : la frontiere publique du service. |
| `app/errors.py` | `ApiError` et les gestionnaires qui garantissent un corps d'erreur unique. |
| `app/ollama_client.py` | Client `POST /api/embed`, traduction des pannes Ollama en codes du contrat. |
| `app/index.py` | Index numpy, pre-filtrage, persistance disque, absence deliberee de controle de modele. |
| `app/main.py` | Routes et cycle de vie (rechargement de l'index, prechauffage du modele). |
