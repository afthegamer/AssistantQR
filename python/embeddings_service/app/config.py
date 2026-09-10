"""Configuration du service : variables d'environnement et catalogue de modeles.

POURQUOI CE FICHIER EXISTE
--------------------------
Le choix du modele d'embeddings n'est pas un detail d'implementation cache : il
change les vecteurs, donc l'index, donc les documents retrouves, donc la reponse
finale. On le remonte donc au niveau d'une variable d'environnement explicite,
et on l'expose ensuite dans /health et /index/metadata pour que le client puisse
constater le desaccord.
"""

from __future__ import annotations

import os
from dataclasses import dataclass
from pathlib import Path

# Racine du paquet : .../python/embeddings_service
PACKAGE_ROOT = Path(__file__).resolve().parent.parent


@dataclass(frozen=True)
class EmbeddingModelSpec:
    """Description d'un modele d'embeddings supporte.

    PRINCIPE CACE (« Changing Anything Changes Everything »)
    -------------------------------------------------------
    `query_instruction` est l'illustration la plus courte du principe. Les deux
    modeles supportes ici produisent des vecteurs de dimension 1024 : du point de
    vue des types, ils sont interchangeables, et rien dans la signature de
    /embed ne change quand on passe de l'un a l'autre.

    Sauf que qwen3-embedding a ete entraine avec une consigne en tete des
    requetes (« Instruct: ... \nQuery: ... ») et en tire un gain reel, alors que
    bge-m3 n'en a pas et se degrade si on lui en colle une. Autrement dit : une
    variable d'environnement qui ne touche aucune interface modifie quand meme
    silencieusement la qualite de toute la chaine en aval. C'est exactement le
    genre de couplage invisible que le cours veut rendre visible — d'ou le fait
    que le prefixe soit declare ICI, en donnee, et non enfoui dans un `if` au
    fond du client Ollama.
    """

    name: str
    dimension: int
    query_instruction: str | None


# Les deux modeles interchangeables imposes par le sujet. Meme dimension (1024),
# donc l'index accepte techniquement les vecteurs de l'un dans un index construit
# par l'autre : c'est la panne silencieuse que la demonstration met en scene.
SUPPORTED_MODELS: dict[str, EmbeddingModelSpec] = {
    "qwen3-embedding:0.6b": EmbeddingModelSpec(
        name="qwen3-embedding:0.6b",
        dimension=1024,
        query_instruction=(
            "Instruct: Retrouve les passages de documentation qui repondent a la question\n"
            "Query: "
        ),
    ),
    "bge-m3": EmbeddingModelSpec(
        name="bge-m3",
        dimension=1024,
        # bge-m3 est entraine sans consigne : on ne lui en met pas.
        query_instruction=None,
    ),
}

DEFAULT_MODEL = "qwen3-embedding:0.6b"


def _env_bool(name: str, default: bool) -> bool:
    raw = os.environ.get(name)
    if raw is None or raw.strip() == "":
        return default
    return raw.strip().lower() in {"1", "true", "yes", "on", "oui"}


def _env_int(name: str, default: int) -> int:
    raw = os.environ.get(name)
    if raw is None or raw.strip() == "":
        return default
    try:
        return int(raw)
    except ValueError as exc:  # configuration invalide : on echoue au demarrage
        raise RuntimeError(
            f"La variable d'environnement {name} doit etre un entier (valeur lue : {raw!r})."
        ) from exc


def resolve_model(name: str) -> EmbeddingModelSpec:
    """Retourne la fiche du modele demande.

    Un modele inconnu est accepte uniquement si EMBEDDING_DIMENSION est fourni :
    on ne devine jamais une dimension, parce qu'une dimension fausse se traduit
    par un index inutilisable qu'aucune erreur ne signale.
    """
    normalized = name.strip()
    known = SUPPORTED_MODELS.get(normalized)
    if known is not None:
        return known

    declared_dimension = os.environ.get("EMBEDDING_DIMENSION", "").strip()
    if declared_dimension:
        return EmbeddingModelSpec(
            name=normalized,
            dimension=int(declared_dimension),
            query_instruction=os.environ.get("EMBEDDING_QUERY_INSTRUCTION") or None,
        )

    supported = ", ".join(sorted(SUPPORTED_MODELS))
    raise RuntimeError(
        f"Modele d'embeddings inconnu : {normalized!r}. "
        f"Modeles supportes : {supported}. "
        "Pour en utiliser un autre, declare aussi EMBEDDING_DIMENSION."
    )


@dataclass(frozen=True)
class Settings:
    """Etat de configuration fige au demarrage du processus."""

    model: EmbeddingModelSpec
    ollama_url: str
    ollama_timeout_seconds: float
    data_dir: Path
    warmup: bool
    host: str
    port: int

    @property
    def query_instruction(self) -> str | None:
        return self.model.query_instruction


def load_settings() -> Settings:
    """Construit les reglages a partir de l'environnement (voir .env.example)."""
    model = resolve_model(os.environ.get("EMBEDDING_MODEL", DEFAULT_MODEL))

    raw_data_dir = os.environ.get("DATA_DIR", "").strip()
    data_dir = Path(raw_data_dir) if raw_data_dir else PACKAGE_ROOT / "data"

    return Settings(
        model=model,
        ollama_url=os.environ.get("OLLAMA_URL", "http://localhost:11434").rstrip("/"),
        ollama_timeout_seconds=float(_env_int("OLLAMA_TIMEOUT_SECONDS", 120)),
        data_dir=data_dir,
        # Le prechauffage est le premier argument en faveur du service HTTP :
        # le modele est charge une fois ici, pas a chaque appel du C#.
        warmup=_env_bool("WARMUP_ON_STARTUP", True),
        host=os.environ.get("SERVICE_HOST", "0.0.0.0"),
        port=_env_int("SERVICE_PORT", 8088),
    )
