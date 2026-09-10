#!/usr/bin/env bash
# Verifie a la main le contrat HTTP du service d'embeddings.
#
# C'est le troisieme argument en faveur du service HTTP plutot que d'un
# sous-processus : on diagnostique toute la couche Python au curl, sans SDK .NET
# installe, et on sait immediatement de quel cote du pont se trouve le probleme.
#
# Usage :
#   ./scripts/smoke-test.sh                          # lecture seule
#   ./scripts/smoke-test.sh http://localhost:8088    # autre adresse
#   ./scripts/smoke-test.sh --write                  # DETRUIT l'index en place

set -uo pipefail

BASE="http://localhost:8088"
WRITE=0
for arg in "$@"; do
  case "$arg" in
    --write) WRITE=1 ;;
    http*) BASE="${arg%/}" ;;
    -h|--help) sed -n '2,12p' "$0"; exit 0 ;;
    *) echo "Argument inconnu : $arg" >&2; exit 2 ;;
  esac
done

# Sous Windows, `python3` peut exister sans etre un interpreteur : c'est le
# raccourci du Microsoft Store. On verifie donc qu'il s'execute vraiment.
PY=""
for candidate in "${PYTHON_BIN:-}" python3 python py; do
  [ -z "$candidate" ] && continue
  if command -v "$candidate" >/dev/null 2>&1 && "$candidate" -c "import sys" >/dev/null 2>&1; then
    PY="$candidate"
    break
  fi
done
if [ -z "$PY" ]; then
  echo "Python est requis pour lire les reponses JSON de ce script." >&2
  exit 1
fi
if ! command -v curl >/dev/null 2>&1; then
  echo "curl est introuvable." >&2
  exit 1
fi

FAILURES=0
STATUS=""
BODY=""

ok()   { printf '  [ok]   %s\n' "$1"; }
warn() { printf '  [!]    %s\n' "$1"; }
ko()   { printf '  [KO]   %s\n' "$1"; FAILURES=$((FAILURES + 1)); }
step() { printf '\n=== %s\n' "$1"; }

# Les corps transitent par des variables et des tubes, jamais par des fichiers
# temporaires : sous Git Bash, curl.exe ne sait pas lire un chemin /tmp/....
api() {
  local method="$1" path="$2" body="${3:-}" raw
  if [ -n "$body" ]; then
    raw="$(printf '%s' "$body" | curl -sS -w $'\n%{http_code}' -X "$method" \
      -H 'Content-Type: application/json' --data-binary @- "$BASE$path" 2>/dev/null)" || raw=$'\n000'
  else
    raw="$(curl -sS -w $'\n%{http_code}' -X "$method" "$BASE$path" 2>/dev/null)" || raw=$'\n000'
  fi
  STATUS="${raw##*$'\n'}"
  BODY="${raw%$'\n'*}"
}

field()  { printf '%s' "$BODY" | "$PY" -c "import json,sys;print(json.load(sys.stdin).get(sys.argv[1],''))" "$1" 2>/dev/null; }
efield() { printf '%s' "$BODY" | "$PY" -c "import json,sys;print(json.load(sys.stdin).get('error',{}).get(sys.argv[1],''))" "$1" 2>/dev/null; }
hits()   { printf '%s' "$BODY" | "$PY" -c "import json,sys;print(', '.join(f\"{r['chunk_id']} ({r['score']:.3f})\" for r in json.load(sys.stdin)['results']) or '(aucun)')" 2>/dev/null; }
ids()    { printf '%s' "$BODY" | "$PY" -c "import json,sys;print(','.join(r['chunk_id'] for r in json.load(sys.stdin)['results']))" 2>/dev/null; }

# Fabrique des corps de requete : des vecteurs unitaires donnent des scores
# previsibles, donc une demonstration de pre-filtrage verifiable a l'oeil.
GEN_SCRIPT='
import json, sys

kind = sys.argv[1]
dimension = int(sys.argv[2])
vector = sys.argv[3] if len(sys.argv) > 3 else ""

def unit(*axes):
    values = [0.0] * dimension
    for axis in axes:
        values[axis] = 1.0
    norm = sum(v * v for v in values) ** 0.5 or 1.0
    return [v / norm for v in values]

def chunk(chunk_id, level, values):
    document = chunk_id.split("#")[0]
    return {
        "chunk_id": chunk_id, "document_id": document,
        "document_title": "Document " + document, "access_level": level,
        "ordinal": 0, "text": "Texte factice du chunk " + chunk_id, "vector": values,
    }

if kind == "reset":
    payload = {"model": "smoke-test", "dimension": dimension, "chunking_strategy_id": "smoke-test"}
elif kind == "upsert":
    payload = {"items": [
        chunk("secret#0", "confidential", unit(0)),
        chunk("interne#0", "internal", unit(0, 1)),
        chunk("public#0", "public", unit(0, 1, 2)),
    ]}
elif kind == "search":
    level = vector if vector else None
    payload = {"vector": unit(0), "top_k": 2, "max_access_level": level}
elif kind == "search-from-embed":
    payload = {"vector": json.load(sys.stdin)["vectors"][0], "top_k": 4, "max_access_level": None}
else:
    raise SystemExit("kind inconnu : " + kind)

print(json.dumps(payload))
'
gen() { "$PY" -c "$GEN_SCRIPT" "$@"; }

echo "Service vise : $BASE"

# ------------------------------------------------------------------ /health
step "GET /health"
api GET /health
if [ "$STATUS" != "200" ]; then
  ko "code $STATUS - le service ne repond pas. Lance ./scripts/start-embeddings.sh"
  exit 1
fi
MODEL="$(field embedding_model)"
DIM="$(field dimension)"
REACHABLE="$(field ollama_reachable)"
ok "modele courant : $MODEL (dimension $DIM)"
ok "chunks indexes : $(field index_chunk_count)"
if [ "$REACHABLE" = "True" ]; then
  ok "ollama joignable"
else
  warn "ollama injoignable (les appels /embed vont echouer)"
fi

# --------------------------------------------------------- /index/metadata
step "GET /index/metadata"
api GET /index/metadata
if [ "$STATUS" != "200" ]; then
  ko "code $STATUS"
else
  INDEX_MODEL="$(field embedding_model)"
  ok "index construit par : ${INDEX_MODEL:-(jamais construit)}"
  ok "decoupage : $(field chunking_strategy_id)  built_at : $(field built_at)"
  if [ -n "$INDEX_MODEL" ] && [ "$INDEX_MODEL" != "$MODEL" ]; then
    warn "DESACCORD DE MODELE : index construit par $INDEX_MODEL, service configure sur $MODEL."
    warn "Le service ne refusera rien (dimensions egales) : c'est la panne silencieuse. Reindexe."
  fi
fi

# ------------------------------------------------------------------- /embed
step "POST /embed (kind=query)"
api POST /embed '{"texts": ["Quels sont les horaires d'"'"'ouverture le samedi ?"], "kind": "query"}'
QUERY_BODY=""
if [ "$STATUS" = "200" ]; then
  ok "vecteur obtenu : dimension $(field dimension), modele $(field model)"
  QUERY_BODY="$(printf '%s' "$BODY" | gen search-from-embed "$DIM")"
else
  ERROR_CODE="$(efield code)"
  if [ "$ERROR_CODE" = "OLLAMA_UNREACHABLE" ] || [ "$ERROR_CODE" = "MODEL_NOT_FOUND" ]; then
    warn "code $STATUS / $ERROR_CODE : $(efield message)"
    warn "conseil : $(efield hint)"
  else
    ko "code $STATUS / $ERROR_CODE : $(efield message)"
  fi
fi

# ------------------------------------------------------------ /index/search
step "POST /index/search"
if [ -n "$QUERY_BODY" ]; then
  api POST /index/search "$QUERY_BODY"
  if [ "$STATUS" = "200" ]; then
    ok "resultats : $(hits)"
  elif [ "$(efield code)" = "INDEX_EMPTY" ]; then
    warn "index vide : lance d'abord l'indexation du corpus (commande index du CLI)."
  else
    ko "code $STATUS / $(efield code) : $(efield message)"
  fi
else
  warn "ignore : pas de vecteur de requete disponible."
fi

# ------------------------------------------ ecriture (optionnelle, destructive)
if [ "$WRITE" = "1" ]; then
  step "POST /index/reset + /index/upsert + /index/search  (MODE ECRITURE)"
  warn "l'index en place est REMPLACE par trois chunks factices ; il faudra reindexer."

  api POST /index/reset "$(gen reset "$DIM")"
  [ "$STATUS" = "200" ] && ok "reset : $BODY" || ko "reset code $STATUS : $(efield message)"

  api POST /index/upsert "$(gen upsert "$DIM")"
  [ "$STATUS" = "200" ] && ok "upsert : $BODY" || ko "upsert code $STATUS : $(efield message)"

  api POST /index/search "$(gen search "$DIM")"
  if [ "$STATUS" = "200" ]; then
    ok "sans filtre (top 2) : $(ids)"
  else
    ko "search code $STATUS : $(efield message)"
  fi

  api POST /index/search "$(gen search "$DIM" public)"
  if [ "$STATUS" = "200" ]; then
    FILTERED="$(ids)"
    ok "pre-filtre public (top 2) : $FILTERED"
    if [ "$FILTERED" = "public#0" ]; then
      ok "le filtrage a bien eu lieu AVANT le top-k (sinon la liste serait vide)."
    else
      ko "resultat inattendu du pre-filtrage : $FILTERED"
    fi
  else
    ko "search filtre code $STATUS : $(efield message)"
  fi
fi

echo
if [ "$FAILURES" -eq 0 ]; then
  echo "Contrat HTTP verifie sans echec."
else
  echo "$FAILURES verification(s) en echec." >&2
fi
exit "$FAILURES"
