"""Schemas pydantic des corps entrants et sortants.

POURQUOI CE FICHIER EXISTE
--------------------------
C'est la frontiere du service : tout ce qui est declare ici est public et fige
par la section 7 du contrat technique. Les noms de champs sont en snake_case
parce que le client C# deserialise avec `JsonNamingPolicy.SnakeCaseLower` ;
renommer un champ ici casse la compilation de personne mais casse l'integration
au premier appel, ce qui est pire.

Volontairement, la validation reste ici purement structurelle (types, presence).
Les regles qui doivent produire un code d'erreur du contrat (EMPTY_INPUT,
DIMENSION_MISMATCH, INVALID_ACCESS_LEVEL) sont verifiees dans les routes, pas
par pydantic : sinon elles sortiraient en INVALID_REQUEST et le C# ne pourrait
plus distinguer « tu m'as envoye zero texte » de « ton JSON est illisible ».
"""

from __future__ import annotations

from typing import Literal

from pydantic import BaseModel, Field

EmbeddingKind = Literal["query", "document"]


class EmbedRequest(BaseModel):
    """Corps de POST /embed."""

    texts: list[str] = Field(default_factory=list)
    # `kind` existe parce que certains modeles traitent une requete et un
    # document differemment (cf. la consigne de qwen3 dans config.py).
    kind: EmbeddingKind = "document"


class EmbedResponse(BaseModel):
    model: str
    dimension: int
    vectors: list[list[float]]


class ResetRequest(BaseModel):
    """Corps de POST /index/reset."""

    model: str
    dimension: int
    chunking_strategy_id: str


class OkResponse(BaseModel):
    ok: bool = True


class UpsertItem(BaseModel):
    """Un chunk et son vecteur.

    Les metadonnees voyagent avec le vecteur : l'index doit pouvoir rendre un
    resultat exploitable (titre, niveau, texte) sans que le client ait a
    recharger le corpus pour reconstituer une citation.
    """

    chunk_id: str
    document_id: str
    document_title: str
    # Le niveau circule en anglais : "public" | "internal" | "confidential".
    # La traduction depuis le francais du corpus est faite cote C#.
    access_level: str
    ordinal: int
    text: str
    vector: list[float]


class UpsertRequest(BaseModel):
    items: list[UpsertItem] = Field(default_factory=list)


class UpsertResponse(BaseModel):
    """`count` = nombre d'elements ecrits par CET appel, pas la taille de l'index."""

    count: int


class SearchRequest(BaseModel):
    """Corps de POST /index/search."""

    vector: list[float] = Field(default_factory=list)
    top_k: int = 4
    # None = aucun filtre. Quand il est fourni, le filtrage a lieu AVANT le
    # top-k (voir index.py) : c'est le pre-filtrage, et ce champ est la trace
    # visible d'une regle metier qui a fui dans l'infrastructure.
    max_access_level: str | None = None


class SearchResult(BaseModel):
    chunk_id: str
    document_id: str
    document_title: str
    access_level: str
    ordinal: int
    text: str
    score: float


class SearchResponse(BaseModel):
    results: list[SearchResult]


class HealthResponse(BaseModel):
    status: str
    embedding_model: str
    dimension: int
    ollama_reachable: bool
    index_chunk_count: int


class MetadataResponse(BaseModel):
    """Corps de GET /index/metadata.

    `embedding_model` est le modele qui a CONSTRUIT l'index, pas celui que le
    service utilise aujourd'hui. Les deux peuvent differer, et c'est justement
    ce que le client doit pouvoir constater.
    """

    embedding_model: str
    dimension: int
    chunking_strategy_id: str
    chunk_count: int
    # ISO-8601 avec fuseau, ou null si l'index n'a jamais ete construit.
    built_at: str | None = None
