"""Erreurs applicatives et leur traduction en corps JSON.

POURQUOI CE FICHIER EXISTE
--------------------------
C'est la moitie visible du premier argument en faveur du service HTTP : une
panne sort d'ici sous la forme d'un code de statut et d'un objet JSON structure
`{"error": {"code", "message", "hint"}}`, que le C# peut deserialiser et
transformer en exception lisible. Avec un sous-processus, la meme panne serait
une trace Python sur stderr, a decouper a la main.

Les codes sont figes par la section 7 du contrat technique : le C# les compare
litteralement.
"""

from __future__ import annotations

import logging
from typing import Any

from fastapi import FastAPI, Request
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse

_logger = logging.getLogger(__name__)

# Codes du contrat (section 7). INVALID_REQUEST est le seul ajout : il couvre le
# JSON malforme, cas que le contrat ne nommait pas mais que FastAPI produit.
CODE_OLLAMA_UNREACHABLE = "OLLAMA_UNREACHABLE"
CODE_MODEL_NOT_FOUND = "MODEL_NOT_FOUND"
CODE_DIMENSION_MISMATCH = "DIMENSION_MISMATCH"
CODE_EMPTY_INPUT = "EMPTY_INPUT"
CODE_INDEX_EMPTY = "INDEX_EMPTY"
CODE_INVALID_ACCESS_LEVEL = "INVALID_ACCESS_LEVEL"
CODE_INVALID_REQUEST = "INVALID_REQUEST"
CODE_INTERNAL_ERROR = "INTERNAL_ERROR"


class ApiError(Exception):
    """Panne attendue, portant deja son code de statut et son code metier.

    Le champ `hint` n'est pas decoratif : c'est la phrase qui dit a l'humain
    quoi taper pour s'en sortir (« lance ollama pull ... »). Il traverse le
    reseau et remonte tel quel dans les exceptions C#.
    """

    def __init__(self, status_code: int, code: str, message: str, hint: str = "") -> None:
        super().__init__(message)
        self.status_code = status_code
        self.code = code
        self.message = message
        self.hint = hint

    def to_payload(self) -> dict[str, Any]:
        return {"error": {"code": self.code, "message": self.message, "hint": self.hint}}

    def to_response(self) -> JSONResponse:
        return JSONResponse(status_code=self.status_code, content=self.to_payload())


def empty_input(message: str, hint: str = "") -> ApiError:
    return ApiError(400, CODE_EMPTY_INPUT, message, hint)


def dimension_mismatch(message: str, hint: str = "") -> ApiError:
    return ApiError(400, CODE_DIMENSION_MISMATCH, message, hint)


def invalid_access_level(message: str, hint: str = "") -> ApiError:
    return ApiError(400, CODE_INVALID_ACCESS_LEVEL, message, hint)


def index_empty(message: str, hint: str = "") -> ApiError:
    return ApiError(409, CODE_INDEX_EMPTY, message, hint)


def register_error_handlers(app: FastAPI) -> None:
    """Branche les trois gestionnaires qui garantissent un corps d'erreur unique.

    Toute sortie non-2xx du service doit avoir la meme forme, sinon le
    deserialiseur C# doit gerer deux formats et on perd le benefice du choix HTTP.
    """

    @app.exception_handler(ApiError)
    async def _handle_api_error(_: Request, exc: ApiError) -> JSONResponse:
        return exc.to_response()

    @app.exception_handler(RequestValidationError)
    async def _handle_validation_error(_: Request, exc: RequestValidationError) -> JSONResponse:
        # FastAPI renverrait un 422 avec sa propre structure : on la remplace
        # pour que le client n'ait qu'un seul format d'erreur a connaitre.
        details = "; ".join(
            f"{'.'.join(str(part) for part in error.get('loc', ()))} : {error.get('msg', '')}"
            for error in exc.errors()
        )
        error = ApiError(
            400,
            CODE_INVALID_REQUEST,
            f"Corps de requete invalide. {details}".strip(),
            "Verifie les noms de champs : le contrat impose du snake_case (top_k, max_access_level).",
        )
        return error.to_response()

    @app.exception_handler(Exception)
    async def _handle_unexpected(_: Request, exc: Exception) -> JSONResponse:
        _logger.exception("Erreur inattendue")
        error = ApiError(
            500,
            CODE_INTERNAL_ERROR,
            f"Erreur interne du service d'embeddings : {exc}",
            "Consulte les journaux du service (uvicorn) pour la trace complete.",
        )
        return error.to_response()
