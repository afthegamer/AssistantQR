#!/usr/bin/env bash
# Telecharge les modeles Ollama utilises par AssistantQR.
#
#   granite4.2:3b         modele de generation (les reponses citees)
#   qwen3-embedding:0.6b  embeddings, dimension 1024, aime les consignes
#   bge-m3                embeddings, dimension 1024, n'en veut pas
#
# Les deux modeles d'embeddings sont volontairement interchangeables : c'est
# avec eux qu'on met en scene la panne silencieuse (interroger un index construit
# par l'un avec des vecteurs produits par l'autre).
#
# Compter plusieurs gigaoctets de telechargement au premier appel.

set -uo pipefail

MODELS=(
  "granite4.2:3b"
  "qwen3-embedding:0.6b"
  "bge-m3"
)

if ! command -v ollama >/dev/null 2>&1; then
  echo "La commande 'ollama' est introuvable." >&2
  echo "Installe Ollama (https://ollama.com/download), ou passe par le conteneur :" >&2
  echo "  docker compose --profile ollama up -d" >&2
  echo "  docker compose --profile ollama exec ollama ollama pull qwen3-embedding:0.6b" >&2
  exit 1
fi

failures=0
for model in "${MODELS[@]}"; do
  echo "=== ollama pull $model"
  if ! ollama pull "$model"; then
    echo "Echec du telechargement de $model." >&2
    failures=$((failures + 1))
  fi
done

echo
if [ "$failures" -eq 0 ]; then
  echo "Les ${#MODELS[@]} modeles sont installes. Verification :"
  ollama list
else
  echo "$failures modele(s) n'ont pas pu etre telecharges." >&2
  exit 1
fi
