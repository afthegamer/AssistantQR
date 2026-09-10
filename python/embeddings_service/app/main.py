"""Routes HTTP du service d'embeddings et cycle de vie de l'application.

POURQUOI UN SERVICE HTTP ET PAS UN SOUS-PROCESSUS
-------------------------------------------------
La decision est prise, ce commentaire ne la rediscute pas : il l'explique, parce
que trois choses dans ce fichier n'existeraient pas autrement.

1. Les erreurs sont lisibles. Une panne sort d'ici avec un code de statut et un
   corps `{"error": {"code", "message", "hint"}}` que le C# deserialise. En
   sous-processus, la meme panne serait une trace Python melangee au reste de
   stderr, a decouper a coups d'expressions regulieres.
2. Le modele est chauffe une seule fois. Le `lifespan` ci-dessous fait un appel
   d'embedding a vide au demarrage : Ollama charge les poids en memoire une fois
   pour toute la duree du service. Un sous-processus relance par appel paierait
   ce chargement a chaque question.
3. Le service se teste seul. `curl http://localhost:8088/health` suffit a savoir
   si le probleme vient du Python ou du C#, sans SDK .NET installe. Cette
   frontiere nette est aussi ce qui permet aux tests de ce dossier de tourner
   sans Ollama : on injecte une doublure a la place du client.
"""

from __future__ import annotations

import logging
from collections.abc import AsyncIterator
from contextlib import asynccontextmanager

from fastapi import Depends, FastAPI, Request

from .config import Settings, load_settings
from .errors import empty_input, dimension_mismatch, register_error_handlers
from .index import ChunkRecord, IndexEntry, VectorIndex
from .models import (
    EmbedRequest,
    EmbedResponse,
    HealthResponse,
    MetadataResponse,
    OkResponse,
    ResetRequest,
    SearchRequest,
    SearchResponse,
    SearchResult,
    UpsertRequest,
    UpsertResponse,
)
from .ollama_client import EmbeddingBackend, OllamaClient

_logger = logging.getLogger(__name__)

WARMUP_TEXT = "chauffage du modele d'embeddings"


# --------------------------------------------------------------- injections


def get_settings(request: Request) -> Settings:
    return request.app.state.settings


def get_index(request: Request) -> VectorIndex:
    return request.app.state.index


def get_backend(request: Request) -> EmbeddingBackend:
    return request.app.state.backend


# ----------------------------------------------------------- fabrique d'app


def _configure_logging() -> None:
    """Rend visibles les messages de ce paquet.

    uvicorn ne configure que ses propres journaux. Sans ce branchement, les
    lignes qui interessent l'etudiant -- index recharge depuis le disque, modele
    chauffe, prechauffage impossible -- n'apparaitraient pas, ou sortiraient
    sans niveau ni nom de module.
    """
    package_logger = logging.getLogger(__name__.split(".")[0])
    if package_logger.handlers:
        return
    handler = logging.StreamHandler()
    handler.setFormatter(logging.Formatter("%(levelname)-8s %(name)s: %(message)s"))
    package_logger.addHandler(handler)
    package_logger.setLevel(logging.INFO)
    package_logger.propagate = False


def create_app(
    settings: Settings | None = None,
    backend: EmbeddingBackend | None = None,
) -> FastAPI:
    """Construit l'application.

    `backend` est le point d'injection qui rend les tests possibles hors ligne :
    quand il est fourni, aucun client Ollama reel n'est cree.
    """
    _configure_logging()
    resolved = settings or load_settings()
    index = VectorIndex(resolved.data_dir)

    @asynccontextmanager
    async def lifespan(application: FastAPI) -> AsyncIterator[None]:
        application.state.settings = resolved
        application.state.index = index

        owned_client: OllamaClient | None = None
        if backend is None:
            owned_client = OllamaClient(resolved.ollama_url, resolved.ollama_timeout_seconds)
            application.state.backend = owned_client
        else:
            application.state.backend = backend

        # L'index vit sur disque : on le recharge pour qu'un redemarrage du
        # service ne coute pas une reindexation complete du corpus.
        index.load()

        if resolved.warmup and owned_client is not None:
            await _warm_up(owned_client, resolved)

        try:
            yield
        finally:
            if owned_client is not None:
                await owned_client.aclose()

    application = FastAPI(
        title="AssistantQR - service d'embeddings",
        description=(
            "Calcul de vecteurs (via Ollama) et index vectoriel en memoire, "
            "pour l'assistant de questions-reponses AssistantQR."
        ),
        version="1.0.0",
        lifespan=lifespan,
    )

    # Etat pose des la construction : utile si quelqu'un manipule l'app sans
    # passer par le cycle de vie (introspection, outillage).
    application.state.settings = resolved
    application.state.index = index
    application.state.backend = backend

    register_error_handlers(application)
    _register_routes(application)
    return application


async def _warm_up(client: OllamaClient, settings: Settings) -> None:
    """Force le chargement des poids du modele avant la premiere vraie question.

    Un echec ici n'est pas fatal : le service doit demarrer meme si Ollama n'est
    pas encore lance, sinon `docker compose up` devient dependant d'un ordre de
    demarrage. La panne sera signalee, en clair, au premier appel de /embed.
    """
    try:
        await client.embed(settings.model.name, [WARMUP_TEXT])
        _logger.info(
            "Modele %s chauffe (dimension attendue : %d).",
            settings.model.name,
            settings.model.dimension,
        )
    except Exception as exc:  # noqa: BLE001 - le demarrage ne doit jamais echouer ici
        _logger.warning(
            "Prechauffage impossible (%s). Le service demarre quand meme ; "
            "verifie qu'Ollama tourne sur %s et que le modele %s est installe.",
            exc,
            settings.ollama_url,
            settings.model.name,
        )


# --------------------------------------------------------------------- routes


def _register_routes(application: FastAPI) -> None:
    @application.get("/health", response_model=HealthResponse)
    async def health(
        settings: Settings = Depends(get_settings),
        index: VectorIndex = Depends(get_index),
        backend: EmbeddingBackend = Depends(get_backend),
    ) -> HealthResponse:
        """Sonde de vie.

        Repond toujours 200, meme quand Ollama est injoignable : c'est une
        sonde de diagnostic, et une sonde qui tombe en meme temps que ce
        qu'elle surveille ne sert a rien. La verite est dans le champ
        `ollama_reachable`.
        """
        reachable = await backend.is_reachable() if backend is not None else False
        return HealthResponse(
            status="ok",
            embedding_model=settings.model.name,
            dimension=settings.model.dimension,
            ollama_reachable=reachable,
            index_chunk_count=index.chunk_count,
        )

    @application.post("/embed", response_model=EmbedResponse)
    async def embed(
        payload: EmbedRequest,
        settings: Settings = Depends(get_settings),
        backend: EmbeddingBackend = Depends(get_backend),
    ) -> EmbedResponse:
        if not payload.texts:
            raise empty_input(
                "Aucun texte a vectoriser.",
                "Le champ texts doit contenir au moins une chaine non vide.",
            )
        for position, text in enumerate(payload.texts):
            if not text.strip():
                raise empty_input(
                    f"Le texte en position {position} est vide.",
                    "Filtre les chunks vides avant d'appeler /embed.",
                )

        prepared = _apply_query_instruction(payload, settings)
        vectors = await backend.embed(settings.model.name, prepared)

        if len(vectors) != len(payload.texts):
            raise dimension_mismatch(
                f"Le calculateur a renvoye {len(vectors)} vecteurs pour "
                f"{len(payload.texts)} textes.",
                "Reduis la taille des lots envoyes a /embed.",
            )
        for position, vector in enumerate(vectors):
            if len(vector) != settings.model.dimension:
                raise dimension_mismatch(
                    f"Le vecteur en position {position} a une dimension de "
                    f"{len(vector)} alors que {settings.model.name} en annonce "
                    f"{settings.model.dimension}.",
                    "Verifie EMBEDDING_MODEL et EMBEDDING_DIMENSION : le service "
                    "et Ollama ne parlent pas du meme modele.",
                )

        return EmbedResponse(
            model=settings.model.name,
            dimension=settings.model.dimension,
            vectors=vectors,
        )

    @application.post("/index/reset", response_model=OkResponse)
    async def index_reset(
        payload: ResetRequest,
        index: VectorIndex = Depends(get_index),
    ) -> OkResponse:
        """Vide l'index et enregistre qui l'a construit.

        Le modele annonce par le client est stocke tel quel, sans etre confronte
        a EMBEDDING_MODEL. C'est volontaire : le service enregistre un fait, il
        n'arbitre pas (voir VectorIndex.metadata).
        """
        index.reset(payload.model, payload.dimension, payload.chunking_strategy_id)
        _logger.info(
            "Index reinitialise : modele %s, dimension %d, decoupage %s.",
            payload.model,
            payload.dimension,
            payload.chunking_strategy_id,
        )
        return OkResponse(ok=True)

    @application.post("/index/upsert", response_model=UpsertResponse)
    async def index_upsert(
        payload: UpsertRequest,
        index: VectorIndex = Depends(get_index),
    ) -> UpsertResponse:
        entries = [
            IndexEntry(
                record=ChunkRecord(
                    chunk_id=item.chunk_id,
                    document_id=item.document_id,
                    document_title=item.document_title,
                    access_level=item.access_level,
                    ordinal=item.ordinal,
                    text=item.text,
                ),
                vector=item.vector,
            )
            for item in payload.items
        ]
        return UpsertResponse(count=index.upsert(entries))

    @application.post("/index/search", response_model=SearchResponse)
    async def index_search(
        payload: SearchRequest,
        index: VectorIndex = Depends(get_index),
    ) -> SearchResponse:
        """Recherche par similarite cosinus.

        Aucun controle de provenance du vecteur : voir VectorIndex.search et
        VectorIndex.metadata pour le pourquoi de cette permissivite.
        """
        hits = index.search(payload.vector, payload.top_k, payload.max_access_level)
        return SearchResponse(
            results=[
                SearchResult(
                    chunk_id=record.chunk_id,
                    document_id=record.document_id,
                    document_title=record.document_title,
                    access_level=record.access_level,
                    ordinal=record.ordinal,
                    text=record.text,
                    score=score,
                )
                for record, score in hits
            ]
        )

    @application.get("/index/metadata", response_model=MetadataResponse)
    async def index_metadata(index: VectorIndex = Depends(get_index)) -> MetadataResponse:
        raw = index.metadata()
        return MetadataResponse(
            embedding_model=str(raw["embedding_model"]),
            dimension=int(raw["dimension"]),  # type: ignore[arg-type]
            chunking_strategy_id=str(raw["chunking_strategy_id"]),
            chunk_count=int(raw["chunk_count"]),  # type: ignore[arg-type]
            built_at=raw["built_at"],  # type: ignore[arg-type]
        )


def _apply_query_instruction(payload: EmbedRequest, settings: Settings) -> list[str]:
    """Prefixe les requetes quand le modele courant en attend une.

    Le meme texte donne deux vecteurs differents selon `kind` : c'est le
    principe CACE en action (voir config.EmbeddingModelSpec). Les documents ne
    sont jamais prefixes, sinon l'index et les requetes ne vivraient plus dans
    le meme espace.
    """
    instruction = settings.query_instruction
    if payload.kind != "query" or not instruction:
        return list(payload.texts)
    return [f"{instruction}{text}" for text in payload.texts]


# Point d'entree pour uvicorn : `uvicorn app.main:app --port 8088`.
app = create_app()
