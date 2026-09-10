"""Client HTTP minimal pour l'API d'embeddings d'Ollama.

POURQUOI CE FICHIER EXISTE
--------------------------
Le calcul des vecteurs est delegue a Ollama (`POST /api/embed`). On n'embarque
donc ni sentence-transformers, ni torch, ni le moindre poids de modele dans ce
service : il reste une couche de transport et un index. Le corollaire, c'est que
toutes les pannes d'Ollama doivent etre traduites ici en ApiError avec un code
du contrat — sinon elles ressortiraient en 500 opaque cote C#.

Le client est declare derriere un Protocol pour que les tests injectent une
doublure et tournent sans Ollama.
"""

from __future__ import annotations

import logging
from typing import Protocol

import httpx

from .errors import (
    CODE_INTERNAL_ERROR,
    CODE_MODEL_NOT_FOUND,
    CODE_OLLAMA_UNREACHABLE,
    ApiError,
)

_logger = logging.getLogger(__name__)


class EmbeddingBackend(Protocol):
    """Ce que les routes attendent d'un calculateur de vecteurs."""

    async def embed(self, model: str, inputs: list[str]) -> list[list[float]]:
        ...

    async def is_reachable(self) -> bool:
        ...


class OllamaClient:
    """Adaptateur reel vers Ollama."""

    def __init__(
        self,
        base_url: str,
        timeout_seconds: float = 120.0,
        client: httpx.AsyncClient | None = None,
    ) -> None:
        self._base_url = base_url.rstrip("/")
        self._timeout = timeout_seconds
        self._owns_client = client is None
        self._client = client or httpx.AsyncClient(timeout=timeout_seconds)

    @property
    def base_url(self) -> str:
        return self._base_url

    async def aclose(self) -> None:
        if self._owns_client:
            await self._client.aclose()

    async def is_reachable(self) -> bool:
        """Sonde non bloquante : ne leve jamais, /health doit toujours repondre."""
        try:
            response = await self._client.get(f"{self._base_url}/api/tags", timeout=3.0)
            return response.status_code == 200
        except httpx.HTTPError:
            return False

    async def list_models(self) -> list[str]:
        try:
            response = await self._client.get(f"{self._base_url}/api/tags", timeout=5.0)
        except httpx.HTTPError:
            return []
        if response.status_code != 200:
            return []
        payload = response.json()
        return [str(entry.get("model") or entry.get("name") or "") for entry in payload.get("models", [])]

    async def embed(self, model: str, inputs: list[str]) -> list[list[float]]:
        """Appelle POST /api/embed et rend les vecteurs dans l'ordre des entrees."""
        url = f"{self._base_url}/api/embed"
        try:
            response = await self._client.post(
                url,
                json={"model": model, "input": inputs},
                timeout=self._timeout,
            )
        except httpx.HTTPError as exc:
            raise ApiError(
                503,
                CODE_OLLAMA_UNREACHABLE,
                f"Ollama est injoignable sur {self._base_url} ({exc.__class__.__name__}).",
                "Demarre Ollama (`ollama serve`) ou corrige OLLAMA_URL, "
                "puis relance le service d'embeddings.",
            ) from exc

        if response.status_code == 404:
            raise ApiError(
                404,
                CODE_MODEL_NOT_FOUND,
                f"Ollama ne connait pas le modele d'embeddings « {model} ».",
                f"Lance : ollama pull {model}",
            )

        if response.status_code >= 400:
            body = response.text[:500]
            raise ApiError(
                502,
                CODE_INTERNAL_ERROR,
                f"Ollama a repondu {response.status_code} a POST /api/embed : {body}",
                "Verifie les journaux d'Ollama ; le modele est peut-etre en cours de chargement.",
            )

        payload = response.json()
        vectors = payload.get("embeddings")
        if not vectors:
            raise ApiError(
                502,
                CODE_INTERNAL_ERROR,
                "Ollama a repondu 200 mais sans aucun vecteur (champ « embeddings » absent ou vide).",
                "Verifie que le modele demande est bien un modele d'embeddings et non un modele de generation.",
            )
        return [[float(value) for value in vector] for vector in vectors]
