"""Tests du contrat HTTP, sans Ollama.

Le client Ollama est remplace par une doublure. Ce n'est pas une commodite de
test : c'est la consequence directe du choix d'architecture. Le service parle a
Ollama a travers UNE interface (`EmbeddingBackend`), donc on peut la substituer
et verifier tout le contrat -- codes d'erreur compris -- sans qu'aucun modele
soit installe sur la machine.
"""

from __future__ import annotations

import hashlib
import math
import re
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

from app.config import SUPPORTED_MODELS, Settings
from app.errors import ApiError
from app.main import create_app

DIMENSION = 1024
QWEN = "qwen3-embedding:0.6b"
BGE = "bge-m3"


# --------------------------------------------------------------- doublures


class FakeOllama:
    """Calculateur de vecteurs deterministe et hors ligne.

    Il memorise les textes recus : c'est ainsi qu'on verifie que la consigne
    d'instruction de qwen3 est bien appliquee aux requetes et seulement a elles.
    """

    def __init__(
        self,
        dimension: int = DIMENSION,
        reachable: bool = True,
        failure: ApiError | None = None,
        vector_length: int | None = None,
        vector_count: int | None = None,
    ) -> None:
        self.dimension = dimension
        self.reachable = reachable
        self.failure = failure
        self.vector_length = vector_length or dimension
        self.vector_count = vector_count
        self.calls: list[tuple[str, list[str]]] = []

    async def embed(self, model: str, inputs: list[str]) -> list[list[float]]:
        self.calls.append((model, list(inputs)))
        if self.failure is not None:
            raise self.failure
        vectors = [self._vector(text) for text in inputs]
        if self.vector_count is not None:
            vectors = vectors[: self.vector_count]
        return vectors

    async def is_reachable(self) -> bool:
        return self.reachable

    def _vector(self, text: str) -> list[float]:
        values = [0.0] * self.vector_length
        for word in re.findall(r"[a-z0-9]+", text.lower()):
            digest = hashlib.sha1(word.encode("utf-8")).hexdigest()[:8]
            values[int(digest, 16) % self.vector_length] += 1.0
        norm = math.sqrt(sum(value * value for value in values)) or 1.0
        return [value / norm for value in values]

    @property
    def last_inputs(self) -> list[str]:
        return self.calls[-1][1]


def unit(*axes: int, dimension: int = DIMENSION) -> list[float]:
    """Vecteur unitaire porte par quelques axes : des scores previsibles."""
    values = [0.0] * dimension
    for axis in axes:
        values[axis] += 1.0
    norm = math.sqrt(sum(value * value for value in values)) or 1.0
    return [value / norm for value in values]


def item(
    chunk_id: str,
    access_level: str,
    vector: list[float],
    document_id: str | None = None,
    ordinal: int = 0,
) -> dict[str, object]:
    document = document_id or chunk_id.split("#")[0]
    return {
        "chunk_id": chunk_id,
        "document_id": document,
        "document_title": f"Titre de {document}",
        "access_level": access_level,
        "ordinal": ordinal,
        "text": f"Contenu du chunk {chunk_id}.",
        "vector": vector,
    }


# ---------------------------------------------------------------- fixtures


@pytest.fixture
def make_client(tmp_path: Path):
    def _make(
        model_name: str = QWEN,
        backend: FakeOllama | None = None,
        data_dir: Path | None = None,
    ) -> TestClient:
        settings = Settings(
            model=SUPPORTED_MODELS[model_name],
            ollama_url="http://ollama.invalide:11434",
            ollama_timeout_seconds=5.0,
            data_dir=data_dir or (tmp_path / "data"),
            warmup=False,
            host="127.0.0.1",
            port=8088,
        )
        return TestClient(create_app(settings=settings, backend=backend or FakeOllama()))

    return _make


def build_index(client: TestClient, model: str = QWEN, chunking: str = "paragraph") -> None:
    response = client.post(
        "/index/reset",
        json={"model": model, "dimension": DIMENSION, "chunking_strategy_id": chunking},
    )
    assert response.status_code == 200, response.text


def error_of(response) -> dict[str, str]:
    payload = response.json()
    assert "error" in payload, payload
    return payload["error"]


# ------------------------------------------------------------------ /health


def test_health_ollama_injoignable_repond_200_et_signale_le_probleme(make_client) -> None:
    with make_client(backend=FakeOllama(reachable=False)) as client:
        response = client.get("/health")

    assert response.status_code == 200
    body = response.json()
    assert body["status"] == "ok"
    assert body["ollama_reachable"] is False
    assert body["embedding_model"] == QWEN
    assert body["dimension"] == DIMENSION
    assert body["index_chunk_count"] == 0


def test_health_index_construit_compte_les_chunks(make_client) -> None:
    with make_client() as client:
        build_index(client)
        client.post("/index/upsert", json={"items": [item("a#0", "public", unit(1))]})
        body = client.get("/health").json()

    assert body["index_chunk_count"] == 1


# ------------------------------------------------------------------- /embed


def test_embed_liste_vide_retourne_empty_input(make_client) -> None:
    with make_client() as client:
        response = client.post("/embed", json={"texts": [], "kind": "document"})

    assert response.status_code == 400
    assert error_of(response)["code"] == "EMPTY_INPUT"


def test_embed_texte_blanc_retourne_empty_input(make_client) -> None:
    with make_client() as client:
        response = client.post("/embed", json={"texts": ["horaires", "   "], "kind": "document"})

    assert response.status_code == 400
    error = error_of(response)
    assert error["code"] == "EMPTY_INPUT"
    assert "position 1" in error["message"]


def test_embed_kind_query_applique_la_consigne_de_qwen(make_client) -> None:
    backend = FakeOllama()
    with make_client(model_name=QWEN, backend=backend) as client:
        response = client.post("/embed", json={"texts": ["horaires du samedi"], "kind": "query"})

    assert response.status_code == 200
    assert backend.last_inputs[0].startswith("Instruct:")
    assert backend.last_inputs[0].endswith("horaires du samedi")


def test_embed_kind_document_ne_prefixe_jamais(make_client) -> None:
    backend = FakeOllama()
    with make_client(model_name=QWEN, backend=backend) as client:
        client.post("/embed", json={"texts": ["horaires du samedi"], "kind": "document"})

    assert backend.last_inputs == ["horaires du samedi"]


def test_embed_kind_query_avec_bge_m3_ne_prefixe_pas(make_client) -> None:
    """bge-m3 n'a pas ete entraine avec une consigne : lui en mettre une nuit."""
    backend = FakeOllama()
    with make_client(model_name=BGE, backend=backend) as client:
        response = client.post("/embed", json={"texts": ["horaires du samedi"], "kind": "query"})

    assert response.status_code == 200
    assert backend.last_inputs == ["horaires du samedi"]
    assert response.json()["model"] == BGE


def test_embed_reponse_expose_le_modele_et_la_dimension(make_client) -> None:
    with make_client() as client:
        body = client.post("/embed", json={"texts": ["a", "b"], "kind": "document"}).json()

    assert body["model"] == QWEN
    assert body["dimension"] == DIMENSION
    assert len(body["vectors"]) == 2
    assert len(body["vectors"][0]) == DIMENSION


def test_embed_dimension_inattendue_retourne_dimension_mismatch(make_client) -> None:
    backend = FakeOllama(vector_length=384)
    with make_client(backend=backend) as client:
        response = client.post("/embed", json={"texts": ["horaires"], "kind": "document"})

    assert response.status_code == 400
    assert error_of(response)["code"] == "DIMENSION_MISMATCH"


def test_embed_nombre_de_vecteurs_incoherent_retourne_dimension_mismatch(make_client) -> None:
    backend = FakeOllama(vector_count=1)
    with make_client(backend=backend) as client:
        response = client.post("/embed", json={"texts": ["a", "b"], "kind": "document"})

    assert response.status_code == 400
    assert error_of(response)["code"] == "DIMENSION_MISMATCH"


def test_embed_ollama_injoignable_remonte_le_code_et_le_conseil(make_client) -> None:
    failure = ApiError(
        503,
        "OLLAMA_UNREACHABLE",
        "Ollama est injoignable.",
        "Demarre Ollama puis relance le service.",
    )
    with make_client(backend=FakeOllama(failure=failure)) as client:
        response = client.post("/embed", json={"texts": ["horaires"], "kind": "query"})

    assert response.status_code == 503
    error = error_of(response)
    assert error["code"] == "OLLAMA_UNREACHABLE"
    assert error["hint"]


# ------------------------------------------------------------ /index/reset


def test_index_reset_vide_lindex_existant(make_client) -> None:
    with make_client() as client:
        build_index(client)
        client.post("/index/upsert", json={"items": [item("a#0", "public", unit(1))]})
        build_index(client)
        body = client.get("/index/metadata").json()

    assert body["chunk_count"] == 0


def test_index_reset_dimension_nulle_retourne_dimension_mismatch(make_client) -> None:
    with make_client() as client:
        response = client.post(
            "/index/reset",
            json={"model": QWEN, "dimension": 0, "chunking_strategy_id": "paragraph"},
        )

    assert response.status_code == 400
    assert error_of(response)["code"] == "DIMENSION_MISMATCH"


# ----------------------------------------------------------- /index/upsert


def test_index_upsert_avant_reset_retourne_index_empty(make_client) -> None:
    with make_client() as client:
        response = client.post("/index/upsert", json={"items": [item("a#0", "public", unit(1))]})

    assert response.status_code == 409
    assert error_of(response)["code"] == "INDEX_EMPTY"


def test_index_upsert_retourne_le_nombre_ecrit(make_client) -> None:
    with make_client() as client:
        build_index(client)
        response = client.post(
            "/index/upsert",
            json={"items": [item("a#0", "public", unit(1)), item("a#1", "internal", unit(2))]},
        )

    assert response.status_code == 200
    assert response.json() == {"count": 2}


def test_index_upsert_meme_chunk_id_remplace_sans_dupliquer(make_client) -> None:
    with make_client() as client:
        build_index(client)
        client.post("/index/upsert", json={"items": [item("a#0", "public", unit(1))]})
        client.post("/index/upsert", json={"items": [item("a#0", "internal", unit(2))]})
        metadata = client.get("/index/metadata").json()
        results = client.post(
            "/index/search", json={"vector": unit(2), "top_k": 5, "max_access_level": None}
        ).json()["results"]

    assert metadata["chunk_count"] == 1
    assert results[0]["access_level"] == "internal"


def test_index_upsert_niveau_inconnu_retourne_invalid_access_level(make_client) -> None:
    with make_client() as client:
        build_index(client)
        response = client.post(
            "/index/upsert", json={"items": [item("a#0", "confidentiel", unit(1))]}
        )

    assert response.status_code == 400
    error = error_of(response)
    assert error["code"] == "INVALID_ACCESS_LEVEL"
    assert "confidential" in error["hint"]


def test_index_upsert_vecteur_de_mauvaise_dimension_retourne_dimension_mismatch(
    make_client,
) -> None:
    with make_client() as client:
        build_index(client)
        response = client.post(
            "/index/upsert", json={"items": [item("a#0", "public", unit(1, dimension=64))]}
        )

    assert response.status_code == 400
    assert error_of(response)["code"] == "DIMENSION_MISMATCH"


def test_index_upsert_liste_vide_ne_change_rien(make_client) -> None:
    with make_client() as client:
        build_index(client)
        response = client.post("/index/upsert", json={"items": []})

    assert response.status_code == 200
    assert response.json() == {"count": 0}


# ----------------------------------------------------------- /index/search


def test_index_search_index_vide_retourne_index_empty(make_client) -> None:
    with make_client() as client:
        build_index(client)
        response = client.post("/index/search", json={"vector": unit(1), "top_k": 4})

    assert response.status_code == 409
    assert error_of(response)["code"] == "INDEX_EMPTY"


def test_index_search_vecteur_vide_retourne_empty_input(make_client) -> None:
    with make_client() as client:
        build_index(client)
        client.post("/index/upsert", json={"items": [item("a#0", "public", unit(1))]})
        response = client.post("/index/search", json={"vector": [], "top_k": 4})

    assert response.status_code == 400
    assert error_of(response)["code"] == "EMPTY_INPUT"


def test_index_search_trie_par_score_decroissant_et_respecte_top_k(make_client) -> None:
    with make_client() as client:
        build_index(client)
        client.post(
            "/index/upsert",
            json={
                "items": [
                    item("loin#0", "public", unit(300)),
                    item("proche#0", "public", unit(1)),
                    item("moyen#0", "public", unit(1, 2)),
                ]
            },
        )
        results = client.post(
            "/index/search", json={"vector": unit(1), "top_k": 2, "max_access_level": None}
        ).json()["results"]

    assert [entry["chunk_id"] for entry in results] == ["proche#0", "moyen#0"]
    assert results[0]["score"] > results[1]["score"]
    assert results[0]["document_title"] == "Titre de proche"


def test_index_search_prefiltre_avant_le_top_k(make_client) -> None:
    """Le coeur de la demonstration pre-filtrage / post-filtrage.

    Les deux meilleurs scores sont confidentiels. Avec top_k = 2 et une
    habilitation `public`, un filtrage APRES le top-k rendrait zero resultat ;
    le pre-filtrage, lui, va chercher le meilleur chunk parmi les lisibles.
    """
    with make_client() as client:
        build_index(client)
        client.post(
            "/index/upsert",
            json={
                "items": [
                    item("secret-a#0", "confidential", unit(1)),
                    item("secret-b#0", "confidential", unit(1, 2)),
                    item("interne#0", "internal", unit(1, 2, 3)),
                    item("public#0", "public", unit(1, 2, 3, 4)),
                ]
            },
        )
        sans_filtre = client.post(
            "/index/search", json={"vector": unit(1), "top_k": 2, "max_access_level": None}
        ).json()["results"]
        prefiltre = client.post(
            "/index/search", json={"vector": unit(1), "top_k": 2, "max_access_level": "public"}
        ).json()["results"]
        prefiltre_interne = client.post(
            "/index/search", json={"vector": unit(1), "top_k": 2, "max_access_level": "internal"}
        ).json()["results"]

    assert [entry["chunk_id"] for entry in sans_filtre] == ["secret-a#0", "secret-b#0"]
    assert [entry["chunk_id"] for entry in prefiltre] == ["public#0"]
    assert [entry["chunk_id"] for entry in prefiltre_interne] == ["interne#0", "public#0"]


def test_index_search_niveau_inconnu_retourne_invalid_access_level(make_client) -> None:
    with make_client() as client:
        build_index(client)
        client.post("/index/upsert", json={"items": [item("a#0", "public", unit(1))]})
        response = client.post(
            "/index/search", json={"vector": unit(1), "top_k": 4, "max_access_level": "interne"}
        )

    assert response.status_code == 400
    assert error_of(response)["code"] == "INVALID_ACCESS_LEVEL"


def test_index_search_dimension_differente_retourne_dimension_mismatch(make_client) -> None:
    with make_client() as client:
        build_index(client)
        client.post("/index/upsert", json={"items": [item("a#0", "public", unit(1))]})
        response = client.post(
            "/index/search", json={"vector": unit(1, dimension=64), "top_k": 4}
        )

    assert response.status_code == 400
    assert error_of(response)["code"] == "DIMENSION_MISMATCH"


def test_index_search_vecteur_dun_autre_modele_reussit_silencieusement(make_client) -> None:
    """La panne silencieuse, verifiee par un test.

    L'index a ete construit par qwen3 ; on l'interroge avec un service configure
    sur bge-m3. Meme dimension, donc aucune erreur : le service repond 200 et
    rend des resultats. C'est le comportement voulu -- la detection appartient
    au client, a partir de /index/metadata.
    """
    with make_client(model_name=QWEN) as builder:
        data_dir = builder.app.state.index.data_dir
        build_index(builder, model=QWEN)
        builder.post("/index/upsert", json={"items": [item("a#0", "public", unit(1))]})

    with make_client(model_name=BGE, data_dir=data_dir) as client:
        health = client.get("/health").json()
        metadata = client.get("/index/metadata").json()
        response = client.post("/index/search", json={"vector": unit(1), "top_k": 4})

    assert response.status_code == 200
    assert len(response.json()["results"]) == 1
    # Rien ne signale le desaccord : seule la confrontation des deux champs
    # ci-dessous permet au client de le decouvrir.
    assert health["embedding_model"] == BGE
    assert metadata["embedding_model"] == QWEN


# --------------------------------------------------------- /index/metadata


def test_index_metadata_index_neuf_expose_des_valeurs_vides(make_client) -> None:
    with make_client() as client:
        body = client.get("/index/metadata").json()

    assert body == {
        "embedding_model": "",
        "dimension": 0,
        "chunking_strategy_id": "",
        "chunk_count": 0,
        "built_at": None,
    }


def test_index_metadata_expose_le_modele_ayant_construit_lindex(make_client) -> None:
    with make_client() as client:
        build_index(client, model=BGE, chunking="fixed-600-100")
        client.post("/index/upsert", json={"items": [item("a#0", "public", unit(1))]})
        body = client.get("/index/metadata").json()

    assert body["embedding_model"] == BGE
    assert body["chunking_strategy_id"] == "fixed-600-100"
    assert body["dimension"] == DIMENSION
    assert body["chunk_count"] == 1
    assert body["built_at"] is not None
    assert body["built_at"].endswith("+00:00")


# -------------------------------------------------------------- persistance


def test_index_survit_a_un_redemarrage_du_service(make_client, tmp_path: Path) -> None:
    data_dir = tmp_path / "persistant"

    with make_client(data_dir=data_dir) as first:
        build_index(first)
        first.post(
            "/index/upsert",
            json={"items": [item("a#0", "public", unit(1)), item("b#0", "internal", unit(2))]},
        )

    with make_client(data_dir=data_dir) as second:
        metadata = second.get("/index/metadata").json()
        results = second.post("/index/search", json={"vector": unit(2), "top_k": 1}).json()

    assert metadata["chunk_count"] == 2
    assert metadata["embedding_model"] == QWEN
    assert results["results"][0]["chunk_id"] == "b#0"


# ------------------------------------------------------- forme des erreurs


def test_corps_json_invalide_retourne_une_erreur_de_forme_unique(make_client) -> None:
    with make_client() as client:
        response = client.post("/index/search", json={"vector": "pas un tableau", "top_k": 4})

    assert response.status_code == 400
    error = error_of(response)
    assert error["code"] == "INVALID_REQUEST"
    assert set(error) == {"code", "message", "hint"}


def test_toutes_les_erreurs_ont_les_trois_memes_champs(make_client) -> None:
    """Un seul format d'erreur : c'est ce qui rend le client C# simple a ecrire."""
    with make_client() as client:
        responses = [
            client.post("/embed", json={"texts": []}),
            client.post("/index/search", json={"vector": unit(1), "top_k": 4}),
            client.post(
                "/index/reset",
                json={"model": QWEN, "dimension": -1, "chunking_strategy_id": "paragraph"},
            ),
        ]

    for response in responses:
        assert response.status_code >= 400
        assert set(response.json()["error"]) == {"code", "message", "hint"}
