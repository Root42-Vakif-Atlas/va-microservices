import base64

import pytest
from fastapi.testclient import TestClient

from app.engine import ModerationEngine
from app.language import LanguageDetector
from app.main import create_app
from app.model import ModelInfo
from tests.conftest import jpeg

CATEGORIES = ("toxicity", "severe_toxicity", "obscene", "identity_attack", "insult", "threat", "sexual_explicit")


class FakeScorer:
    """Gerçek model yerine: metinde "kötü" geçiyorsa yüksek skor."""

    info = ModelInfo("detoxify-multilingual", "test", CATEGORIES)

    def __init__(self) -> None:
        self.calls: list[list[str]] = []

    def score(self, texts: list[str]) -> list[dict[str, float]]:
        self.calls.append(texts)
        return [{c: (0.9 if "kötü" in t and c in ("toxicity", "insult") else 0.01) for c in CATEGORIES} for t in texts]


@pytest.fixture
def scorer() -> FakeScorer:
    return FakeScorer()


@pytest.fixture
def client(scorer: FakeScorer, vision, references, embedder, context):
    engine = ModerationEngine(scorer, LanguageDetector(), vision, references, embedder, context)
    with TestClient(create_app(lambda: engine)) as test_client:
        yield test_client


@pytest.fixture
def text_only_client(scorer: FakeScorer):
    with TestClient(create_app(lambda: ModerationEngine(scorer, LanguageDetector()))) as test_client:
        yield test_client


def _image(color: tuple[int, int, int]) -> str:
    return base64.b64encode(jpeg(color)).decode()


def test_health(client: TestClient) -> None:
    assert client.get("/health/live").status_code == 200
    assert client.get("/health/ready").json() == {"status": "Healthy"}


def test_model_info(client: TestClient) -> None:
    body = client.get("/v1/model").json()
    assert body == {
        "name": "detoxify-multilingual",
        "version": "test",
        "categories": list(CATEGORIES),
        "vision": {"name": "vakifatlas-vision", "version": "test", "nsfw": "nsfw-test", "embedding": "embedding-test"},
        "references": {"version": "test-refs", "works": 2, "images": 4},
        "context": {
            "name": "vakifatlas-context",
            "version": "test",
            "embedding": "embedding-test",
            "index": "test-context",
            "works": 1,
            "prototypes": 5,
            "damagePrototypes": 6,
        },
    }


def test_model_info_without_vision(text_only_client: TestClient) -> None:
    body = text_only_client.get("/v1/model").json()
    assert body["vision"] is None
    assert body["references"] is None
    assert body["context"] is None


def test_classify_returns_scores_per_item_in_camel_case(client: TestClient, scorer: FakeScorer) -> None:
    response = client.post(
        "/v1/classify",
        json={"items": [{"id": "text", "text": "Çok güzel bir avlu, ailemle geldik."}, {"id": "p1", "text": "kötü"}]},
    )
    assert response.status_code == 200
    body = response.json()
    assert body["model"]["version"] == "test"
    first, second = body["results"]
    assert first["id"] == "text"
    assert first["scores"]["toxicity"] == 0.01
    assert first["lexicon"] == []
    assert first["languageSupported"] is True
    assert first["spam"] == 0
    assert first["spamSignals"] == []
    assert first["relevance"] == 0
    assert first["monumentRelevance"] is None
    assert second["id"] == "p1"
    assert second["scores"]["insult"] == 0.9
    assert scorer.calls == [["Çok güzel bir avlu, ailemle geldik.", "kötü"]]


def test_lexicon_hits_raise_scores(client: TestClient) -> None:
    result = client.post("/v1/classify", json={"items": [{"id": "text", "text": "Puşt herif"}]}).json()["results"][0]
    assert result["lexicon"] == [{"category": "insult", "severity": 0.97}]
    assert result["scores"]["toxicity"] == 0.97


def test_spam_rules_are_reported(client: TestClient) -> None:
    text = "Otelimizde %40 indirim, rezervasyon için 0532 000 00 00"
    result = client.post("/v1/classify", json={"items": [{"id": "text", "text": text}]}).json()["results"][0]
    assert result["spam"] == 0.97
    assert result["spamSignals"] == ["contact", "promotion"]
    assert result["scores"]["toxicity"] == 0.01


def test_classify_measures_relevance_against_monument(client: TestClient) -> None:
    body = {
        "monumentId": "yesil-cami",
        "items": [
            {"id": "a", "text": "Camide namaz kıldık."},
            {"id": "b", "text": "Çinileri harika."},
            {"id": "c", "text": "Maç kaç kaç bitti?"},
        ],
    }
    memory, monument, unrelated = client.post("/v1/classify", json=body).json()["results"]
    assert (memory["relevance"], memory["monumentRelevance"]) == (1.0, 0.0)
    assert (monument["relevance"], monument["monumentRelevance"]) == (1.0, 1.0)
    assert (unrelated["relevance"], unrelated["monumentRelevance"]) == (0.0, 0.0)


def test_text_only_engine_reports_null_relevance(text_only_client: TestClient) -> None:
    response = text_only_client.post("/v1/classify", json={"items": [{"id": "t", "text": "Camide namaz kıldık."}]})
    body = response.json()
    assert body["context"] is None
    assert body["results"][0]["relevance"] is None
    assert body["results"][0]["spam"] == 0


def test_unsupported_language_is_reported(client: TestClient) -> None:
    text = "Wir haben die Moschee besucht, sie war wirklich sehr schön."
    result = client.post("/v1/classify", json={"items": [{"id": "text", "text": text}]}).json()["results"][0]
    assert result["language"] == "de"
    assert result["languageSupported"] is False


@pytest.mark.parametrize(
    "body",
    [
        {"items": []},
        {"items": [{"id": "x", "text": ""}]},
        {"items": [{"id": "x", "text": "a"}] * 6},
        {"items": [{"id": "x", "text": "a" * 2001}]},
    ],
)
def test_rejects_invalid_requests(client: TestClient, body: dict) -> None:
    assert client.post("/v1/classify", json=body).status_code == 422


def test_moderate_scores_texts_and_images_together(client: TestClient) -> None:
    body = {
        "monumentId": "yesil-cami",
        "texts": [{"id": "text", "text": "Çinileri çok güzeldi."}, {"id": "photo-0", "text": "kötü"}],
        "images": [{"id": "ph_1", "data": _image((40, 200, 60))}, {"id": "ph_2", "data": _image((250, 30, 30))}],
    }
    response = client.post("/v1/moderate", json=body)
    assert response.status_code == 200
    result = response.json()
    assert result["model"]["version"] == "test"
    assert result["vision"]["version"] == "test"
    assert result["references"]["version"] == "test-refs"
    assert result["context"]["index"] == "test-context"
    assert [t["id"] for t in result["texts"]] == ["text", "photo-0"]
    assert result["texts"][0]["monumentRelevance"] == 1.0
    assert result["texts"][1]["scores"]["insult"] == 0.9
    green, red = result["images"]
    assert green["id"] == "ph_1"
    assert green["readable"] is True
    assert green["nsfw"] == 0.01
    assert green["similarity"] > 0.9
    assert green["references"] == 3
    assert red["nsfw"] == 0.97
    assert red["similarity"] < green["similarity"]


def test_moderate_without_monument_references_returns_null_similarity(client: TestClient) -> None:
    body = {"monumentId": "bilinmeyen-eser", "images": [{"id": "ph_1", "data": _image((40, 200, 60))}]}
    image = client.post("/v1/moderate", json=body).json()["images"][0]
    assert image["similarity"] is None
    assert image["references"] == 0


def test_moderate_reports_unreadable_image_without_failing(client: TestClient) -> None:
    body = {
        "texts": [{"id": "text", "text": "Güzel bir gün"}],
        "images": [{"id": "ph_1", "data": base64.b64encode(b"not an image").decode()}],
    }
    response = client.post("/v1/moderate", json=body)
    assert response.status_code == 200
    assert response.json()["images"][0] == {
        "id": "ph_1",
        "readable": False,
        "nsfw": None,
        "similarity": None,
        "references": 0,
        "catalogCopy": None,
    }


def test_moderate_images_need_vision_models(text_only_client: TestClient) -> None:
    body = {"images": [{"id": "ph_1", "data": _image((40, 200, 60))}]}
    assert text_only_client.post("/v1/moderate", json=body).status_code == 503


def test_moderate_text_only_works_without_vision(text_only_client: TestClient) -> None:
    body = {"texts": [{"id": "text", "text": "Güzel bir gün"}]}
    response = text_only_client.post("/v1/moderate", json=body)
    assert response.status_code == 200
    assert response.json()["images"] == []


@pytest.mark.parametrize(
    "body",
    [
        {},
        {"texts": [], "images": []},
        {"images": [{"id": "x", "data": "not base64!"}]},
        {"images": [{"id": "x", "data": ""}]},
        {"images": [{"id": "x", "data": "QUJD"}] * 5},
        {"monumentId": "x" * 121, "texts": [{"id": "t", "text": "a"}]},
    ],
)
def test_moderate_rejects_invalid_requests(client: TestClient, body: dict) -> None:
    assert client.post("/v1/moderate", json=body).status_code == 422


def test_assess_report_adds_damage_signals_and_catalog_copy(client: TestClient) -> None:
    body = {
        "monumentId": "yesil-cami",
        "texts": [{"id": "text", "text": "Mihraptaki çiniler sökülmüş."}, {"id": "photo-0", "text": "Maç kaç bitti?"}],
        "images": [{"id": "ph_1", "data": _image((40, 200, 60))}],
    }
    response = client.post("/v1/assess-report", json=body)
    assert response.status_code == 200
    result = response.json()
    assert result["context"]["damagePrototypes"] == 6
    damage, unrelated = result["texts"]
    assert damage["damage"] == {"relevance": 0.6, "categories": {"theft": 1.0, "vandalism": 0.0}}
    assert damage["spam"] == 0
    assert unrelated["damage"]["categories"] == {"theft": 0.0, "vandalism": 1.0}
    (image,) = result["images"]
    assert image["catalogCopy"] > 0.95
    assert image["similarity"] > 0.9


def test_assess_report_without_context_returns_null_damage(text_only_client: TestClient) -> None:
    body = {"monumentId": "yesil-cami", "texts": [{"id": "text", "text": "Duvarda çatlak var."}]}
    result = text_only_client.post("/v1/assess-report", json=body).json()
    assert result["texts"][0]["damage"] is None
    assert result["texts"][0]["relevance"] is None


@pytest.mark.parametrize(
    "body",
    [
        {"texts": [{"id": "t", "text": "Duvarda çatlak var."}]},
        {"monumentId": "yesil-cami", "texts": []},
        {"monumentId": "yesil-cami", "images": [{"id": "x", "data": "QUJD"}]},
    ],
)
def test_assess_report_requires_monument_and_text(client: TestClient, body: dict) -> None:
    assert client.post("/v1/assess-report", json=body).status_code == 422


def test_moderate_does_not_add_damage_signals(client: TestClient) -> None:
    body = {"monumentId": "yesil-cami", "texts": [{"id": "t", "text": "Mihraptaki çiniler sökülmüş."}]}
    assert "damage" not in client.post("/v1/moderate", json=body).json()["texts"][0]
