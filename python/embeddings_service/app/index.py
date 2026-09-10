"""Index vectoriel en force brute (numpy) avec persistance sur disque.

POURQUOI CE FICHIER EXISTE
--------------------------
Un corpus de vingt-trois documents pedagogiques tient dans une matrice numpy.
Sortir une base vectorielle pour cela ajouterait un service a demarrer, un
schema a migrer et une couche de magie entre la question et le resultat --
exactement ce que le cours cherche a eviter. Le produit matriciel est ici en
toutes lettres : on voit ce qu'est une recherche par similarite cosinus.

Deux choix de conception meritent d'etre lus avant le code : le pre-filtrage
(voir `search`) et l'absence de controle de modele (voir `metadata`).
"""

from __future__ import annotations

import json
import logging
import threading
from collections.abc import Sequence
from dataclasses import asdict, dataclass
from datetime import datetime, timezone
from pathlib import Path

import numpy as np

from .errors import (
    CODE_INVALID_REQUEST,
    ApiError,
    dimension_mismatch,
    empty_input,
    index_empty,
    invalid_access_level,
)

_logger = logging.getLogger(__name__)

# Le niveau d'acces circule en anglais sur le fil (contrat, section 7).
# Le rang est ce qui permet la comparaison "lisible par" : rang <= habilitation.
ACCESS_RANKS: dict[str, int] = {"public": 0, "internal": 1, "confidential": 2}

_VECTORS_FILE = "index.npy"
_RECORDS_FILE = "index.json"


def normalize_access_level(value: str) -> str:
    """Valide et normalise un niveau d'acces, ou leve INVALID_ACCESS_LEVEL."""
    normalized = (value or "").strip().lower()
    if normalized not in ACCESS_RANKS:
        known = ", ".join(ACCESS_RANKS)
        raise invalid_access_level(
            f"Niveau d'acces inconnu : {value!r}.",
            f"Les niveaux acceptes sont : {known}. "
            "La traduction depuis le francais (interne, confidentiel) se fait cote client.",
        )
    return normalized


@dataclass(frozen=True)
class ChunkRecord:
    """Les metadonnees stockees a cote du vecteur.

    Elles sont dupliquees depuis le corpus a dessein : un resultat de recherche
    doit se suffire a lui-meme pour construire une citation, sans que le client
    ait a relire les fichiers markdown.
    """

    chunk_id: str
    document_id: str
    document_title: str
    access_level: str
    ordinal: int
    text: str


@dataclass(frozen=True)
class IndexEntry:
    """Un enregistrement et son vecteur, tels que les fournit POST /index/upsert."""

    record: ChunkRecord
    vector: Sequence[float]


class VectorIndex:
    """Index plat : une matrice (N, D), une liste de metadonnees, un fichier JSON."""

    def __init__(self, data_dir: Path) -> None:
        self._data_dir = Path(data_dir)
        self._lock = threading.Lock()
        self._vectors: np.ndarray = np.zeros((0, 0), dtype=np.float32)
        self._norms: np.ndarray = np.zeros((0,), dtype=np.float32)
        self._records: list[ChunkRecord] = []
        self._positions: dict[str, int] = {}
        self._embedding_model: str = ""
        self._dimension: int = 0
        self._chunking_strategy_id: str = ""
        self._built_at: str | None = None

    # ------------------------------------------------------------------ etat

    @property
    def chunk_count(self) -> int:
        return len(self._records)

    @property
    def data_dir(self) -> Path:
        return self._data_dir

    def metadata(self) -> dict[str, object]:
        """Expose le modele AYANT CONSTRUIT l'index.

        PANNE SILENCIEUSE, CHOIX ASSUME
        -------------------------------
        Ce service ne compare jamais le modele courant a celui inscrit ici, et
        `search` n'utilise pas cette information. Un vecteur produit par bge-m3
        interroge sans broncher un index construit par qwen3-embedding : meme
        dimension, produit scalaire parfaitement calculable, resultats
        parfaitement plausibles -- et faux.

        C'est deliberement laisse tel quel. Le service expose la matiere
        premiere du diagnostic (`embedding_model`) et laisse au client la
        responsabilite de comparer. Le cours montre ainsi qu'une panne
        d'integration peut n'avoir aucune signature technique : rien ne plante,
        rien n'est journalise, seule la pertinence s'effondre.
        """
        return {
            "embedding_model": self._embedding_model,
            "dimension": self._dimension,
            "chunking_strategy_id": self._chunking_strategy_id,
            "chunk_count": len(self._records),
            "built_at": self._built_at,
        }

    # ------------------------------------------------------------- ecritures

    def reset(self, model: str, dimension: int, chunking_strategy_id: str) -> None:
        """Vide l'index et fige le modele et la dimension pour la suite."""
        if dimension < 1:
            raise dimension_mismatch(
                f"Dimension invalide : {dimension}.",
                "La dimension doit etre un entier strictement positif "
                "(1024 pour les deux modeles supportes).",
            )
        with self._lock:
            self._vectors = np.zeros((0, dimension), dtype=np.float32)
            self._norms = np.zeros((0,), dtype=np.float32)
            self._records = []
            self._positions = {}
            self._embedding_model = model.strip()
            self._dimension = dimension
            self._chunking_strategy_id = chunking_strategy_id.strip()
            self._built_at = datetime.now(timezone.utc).isoformat()
            self._persist()

    def upsert(self, entries: Sequence[IndexEntry]) -> int:
        """Ajoute ou remplace des chunks, apparies par `chunk_id`.

        Retourne le nombre d'elements ecrits par cet appel (pas la taille finale
        de l'index) : le client indexe par lots et veut verifier son lot.
        """
        if not entries:
            return 0

        with self._lock:
            if self._dimension == 0:
                raise index_empty(
                    "L'index n'a pas encore ete initialise.",
                    "Appelle POST /index/reset (modele, dimension, strategie de "
                    "decoupage) avant POST /index/upsert.",
                )

            prepared: list[tuple[ChunkRecord, np.ndarray]] = []
            for entry in entries:
                vector = np.asarray(entry.vector, dtype=np.float32)
                if vector.ndim != 1 or vector.shape[0] != self._dimension:
                    got = vector.shape[0] if vector.ndim == 1 else vector.shape
                    raise dimension_mismatch(
                        f"Le chunk {entry.record.chunk_id!r} porte un vecteur de "
                        f"dimension {got} alors que l'index en attend {self._dimension}.",
                        "Reconstruis l'index avec POST /index/reset si tu as change "
                        "de modele d'embeddings.",
                    )
                record = ChunkRecord(
                    chunk_id=entry.record.chunk_id,
                    document_id=entry.record.document_id,
                    document_title=entry.record.document_title,
                    access_level=normalize_access_level(entry.record.access_level),
                    ordinal=entry.record.ordinal,
                    text=entry.record.text,
                )
                prepared.append((record, vector))

            appended: list[np.ndarray] = []
            for record, vector in prepared:
                position = self._positions.get(record.chunk_id)
                if position is None:
                    self._positions[record.chunk_id] = len(self._records)
                    self._records.append(record)
                    appended.append(vector)
                else:
                    self._records[position] = record
                    self._vectors[position] = vector

            if appended:
                block = np.vstack(appended).astype(np.float32, copy=False)
                self._vectors = (
                    block if self._vectors.shape[0] == 0 else np.vstack([self._vectors, block])
                )

            self._norms = np.linalg.norm(self._vectors, axis=1).astype(np.float32)
            self._built_at = datetime.now(timezone.utc).isoformat()
            self._persist()
            return len(prepared)

    # -------------------------------------------------------------- lectures

    def search(
        self,
        vector: Sequence[float],
        top_k: int,
        max_access_level: str | None = None,
    ) -> list[tuple[ChunkRecord, float]]:
        """Similarite cosinus, en force brute, avec pre-filtrage optionnel.

        LE DILEMME DU PRE-FILTRAGE
        --------------------------
        Quand `max_access_level` est fourni, le masque est applique AVANT le
        top-k : on ecarte d'abord les chunks trop sensibles, puis on prend les k
        meilleurs parmi ce qui reste. C'est la seule facon d'obtenir k candidats
        reellement exploitables -- un post-filtrage peut rendre zero resultat
        alors que le corpus lisible en contenait de bons, simplement parce que
        les k premiers etaient tous confidentiels.

        Le prix a payer est visible dans la signature elle-meme : le controle
        d'acces est une regle metier, et la voila dans le contrat HTTP d'un
        index vectoriel, c'est-a-dire dans l'infrastructure. L'index doit
        desormais connaitre la hierarchie des habilitations. C'est une fuite de
        logique metier assumee, pas un oubli : le client peut choisir de ne pas
        s'en servir (`max_access_level` a null) et filtrer lui-meme apres coup.
        Les deux strategies coexistent precisement pour qu'on puisse comparer
        leurs resultats sur la meme question.
        """
        if top_k < 1:
            raise ApiError(
                400,
                CODE_INVALID_REQUEST,
                f"top_k doit valoir au moins 1 (recu : {top_k}).",
                "Utilise top_k = 4 par defaut.",
            )

        query = np.asarray(list(vector), dtype=np.float32)
        if query.size == 0:
            raise empty_input(
                "Le vecteur de requete est vide.",
                "Appelle d'abord POST /embed avec kind = query pour obtenir un vecteur.",
            )

        with self._lock:
            if not self._records:
                raise index_empty(
                    "L'index vectoriel est vide : aucun chunk n'a ete indexe.",
                    "Lance l'indexation du corpus avant d'interroger l'index.",
                )

            if query.shape[0] != self._dimension:
                # Seul motif legitime de refus : les dimensions ne permettent
                # meme pas de calculer un produit scalaire. Un desaccord de
                # MODELE a dimension egale, lui, passe sans un mot (cf. metadata).
                raise dimension_mismatch(
                    f"Le vecteur de requete a une dimension de {query.shape[0]} "
                    f"alors que l'index en attend {self._dimension}.",
                    "Le modele d'embeddings a change depuis la construction de "
                    "l'index : reconstruis-le, ou repasse au modele indique par "
                    "GET /index/metadata.",
                )

            if max_access_level is None:
                candidates = np.arange(len(self._records), dtype=np.int64)
            else:
                # Pre-filtrage : le masque precede le top-k.
                ceiling = ACCESS_RANKS[normalize_access_level(max_access_level)]
                candidates = np.array(
                    [
                        i
                        for i, record in enumerate(self._records)
                        if ACCESS_RANKS[record.access_level] <= ceiling
                    ],
                    dtype=np.int64,
                )

            if candidates.size == 0:
                return []

            query_norm = float(np.linalg.norm(query))
            if query_norm == 0.0:
                return []

            subset = self._vectors[candidates]
            denominators = self._norms[candidates] * query_norm
            dots = subset @ query
            scores = np.divide(
                dots,
                denominators,
                out=np.zeros_like(dots, dtype=np.float32),
                where=denominators > 0,
            )

            keep = min(top_k, int(scores.shape[0]))
            # Tri decroissant stable : a score egal, l'ordre d'indexation gagne,
            # ce qui rend les instantanes reproductibles.
            order = np.argsort(-scores, kind="stable")[:keep]
            return [
                (self._records[int(candidates[int(i)])], float(scores[int(i)]))
                for i in order
            ]

    # ----------------------------------------------------------- persistance

    def load(self) -> None:
        """Recharge l'index depuis le disque ; un index absent n'est pas une erreur."""
        records_path = self._data_dir / _RECORDS_FILE
        vectors_path = self._data_dir / _VECTORS_FILE
        if not records_path.exists() or not vectors_path.exists():
            _logger.info(
                "Aucun index persiste dans %s : demarrage avec un index vide.", self._data_dir
            )
            return

        try:
            payload = json.loads(records_path.read_text(encoding="utf-8"))
            vectors = np.load(vectors_path).astype(np.float32, copy=False)
        except (OSError, ValueError) as exc:
            _logger.warning("Index persiste illisible (%s) : demarrage avec un index vide.", exc)
            return

        records = [ChunkRecord(**entry) for entry in payload.get("records", [])]
        if vectors.shape[0] != len(records):
            _logger.warning(
                "Index persiste incoherent (%d vecteurs pour %d chunks) : index vide.",
                vectors.shape[0],
                len(records),
            )
            return

        with self._lock:
            self._records = records
            self._vectors = vectors
            self._norms = (
                np.linalg.norm(vectors, axis=1).astype(np.float32)
                if records
                else np.zeros((0,), dtype=np.float32)
            )
            self._positions = {record.chunk_id: i for i, record in enumerate(records)}
            self._embedding_model = str(payload.get("embedding_model", ""))
            self._dimension = int(
                payload.get("dimension", vectors.shape[1] if vectors.ndim == 2 else 0)
            )
            self._chunking_strategy_id = str(payload.get("chunking_strategy_id", ""))
            built_at = payload.get("built_at")
            self._built_at = str(built_at) if built_at else None

        _logger.info(
            "Index recharge : %d chunks, modele %s.",
            len(records),
            self._embedding_model or "(inconnu)",
        )

    def _persist(self) -> None:
        """Ecriture en deux temps (fichier temporaire puis remplacement).

        Sans cela, une interruption au milieu de l'ecriture laisserait un index
        a moitie ecrit que le demarrage suivant chargerait sans le savoir.
        """
        self._data_dir.mkdir(parents=True, exist_ok=True)
        payload = {
            "embedding_model": self._embedding_model,
            "dimension": self._dimension,
            "chunking_strategy_id": self._chunking_strategy_id,
            "built_at": self._built_at,
            "records": [asdict(record) for record in self._records],
        }

        records_tmp = self._data_dir / (_RECORDS_FILE + ".tmp")
        vectors_tmp = self._data_dir / (_VECTORS_FILE + ".tmp")
        records_tmp.write_text(
            json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8"
        )
        with vectors_tmp.open("wb") as handle:
            np.save(handle, self._vectors)
        records_tmp.replace(self._data_dir / _RECORDS_FILE)
        vectors_tmp.replace(self._data_dir / _VECTORS_FILE)
