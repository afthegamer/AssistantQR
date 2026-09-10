#!/usr/bin/env bash
# Demarre le service HTTP d'embeddings sur le port 8088.
#
# Cree l'environnement virtuel s'il manque, installe les dependances epinglees,
# puis lance uvicorn. Le service demarre meme si Ollama n'est pas encore lance :
# la panne se lira alors dans GET /health (ollama_reachable = false).
#
# Usage :
#   ./scripts/start-embeddings.sh                 # modele par defaut
#   EMBEDDING_MODEL=bge-m3 ./scripts/start-embeddings.sh
#   ./scripts/start-embeddings.sh --reload        # rechargement a chaud

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
SERVICE_DIR="$REPO_ROOT/python/embeddings_service"
VENV_DIR="$SERVICE_DIR/.venv"

PORT="${SERVICE_PORT:-8088}"
HOST="${SERVICE_HOST:-127.0.0.1}"

# Sous Windows, `python3` peut exister sans etre un interpreteur : c'est le
# raccourci du Microsoft Store. On verifie donc qu'il s'execute vraiment.
FOUND_PYTHON=""
for candidate in "${PYTHON_BIN:-}" python3 python py; do
  [ -z "$candidate" ] && continue
  if command -v "$candidate" >/dev/null 2>&1 && "$candidate" -c "import sys" >/dev/null 2>&1; then
    FOUND_PYTHON="$candidate"
    break
  fi
done
if [ -z "$FOUND_PYTHON" ]; then
  echo "Python 3.11+ est introuvable. Installe-le ou renseigne PYTHON_BIN." >&2
  exit 1
fi
PYTHON_BIN="$FOUND_PYTHON"

if [ ! -d "$VENV_DIR" ]; then
  echo "Creation de l'environnement virtuel dans $VENV_DIR"
  "$PYTHON_BIN" -m venv "$VENV_DIR"
fi

VENV_PYTHON="$VENV_DIR/bin/python"
if [ ! -x "$VENV_PYTHON" ]; then
  # Environnement cree par un Python Windows (Git Bash, WSL monte sur C:).
  VENV_PYTHON="$VENV_DIR/Scripts/python.exe"
fi

echo "Installation des dependances (versions epinglees)"
"$VENV_PYTHON" -m pip install --disable-pip-version-check --quiet --upgrade pip
"$VENV_PYTHON" -m pip install --disable-pip-version-check --quiet -r "$SERVICE_DIR/requirements.txt"

echo
echo "Service d'embeddings : http://$HOST:$PORT"
echo "  modele  : ${EMBEDDING_MODEL:-qwen3-embedding:0.6b}"
echo "  ollama  : ${OLLAMA_URL:-http://localhost:11434}"
echo "  sonde   : curl http://$HOST:$PORT/health"
echo

cd "$SERVICE_DIR"
exec "$VENV_PYTHON" -m uvicorn app.main:app --host "$HOST" --port "$PORT" "$@"
