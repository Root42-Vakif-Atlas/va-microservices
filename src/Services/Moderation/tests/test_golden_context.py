"""Gerçek bağlam modeli ve indeksle altın örnekler (MODEL_DIR'de context.json + context.npz).

Eşikler ``calibration-context.json``'dan okunur; model, prototipler veya indeks değişince bant kayması burada
yakalanır. Örnekler kalibrasyon setinde ve prototiplerde yoktur.
"""

import json
import os
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[1]
MODEL_DIR = os.environ.get("MODEL_DIR")

pytestmark = [
    pytest.mark.model,
    pytest.mark.skipif(
        not MODEL_DIR or not Path(MODEL_DIR, "context.json").exists() or not Path(MODEL_DIR, "context.npz").exists(),
        reason="context.json / context.npz yok",
    ),
]


@pytest.fixture(scope="module")
def engine():
    from app.context import ContextIndex, OnnxTextEmbedder

    return OnnxTextEmbedder(Path(MODEL_DIR), threads=2), ContextIndex.load(Path(MODEL_DIR, "context.npz"))


@pytest.fixture(scope="module")
def thresholds() -> dict[str, float]:
    return json.loads((ROOT / "calibration-context.json").read_text(encoding="utf-8"))["thresholds"]


def _relevance(engine, text: str, monument_id: str | None):
    embedder, index = engine
    (result,) = index.relevance(embedder.embed([text]), monument_id)
    return result


@pytest.mark.parametrize(
    ("text", "monument_id"),
    [
        ("Ulu Cami'nin yirmi kubbesini tek tek saydık, çocuklar çok eğlendi.", "bursa-ulu-camii"),
        ("Mevlâna'nın türbesinde sessizce dua ettik.", "mevlana-turbesi-ve-mustemilati"),
        ("Hanın avlusundaki çeşmenin başında soluklandık.", "kursunlu-han"),
        ("Bayram sabahı dedemle burada namaz kılardık.", "acemler-camii"),
        ("The courtyard of the mosque was full of pigeons, my son loved it.", "bursa-ulu-camii"),
    ],
)
def test_related_memories_reach_approval(engine, thresholds, text: str, monument_id: str) -> None:
    assert _relevance(engine, text, monument_id).score >= thresholds["approveFrom"]


@pytest.mark.parametrize(
    "text",
    [
        "Fenerbahçe bu sezon ligi kesin alır.",
        "Akşama ne pişirsem bilemedim, makarna mı yapsam?",
        "Yeni dizüstü bilgisayarımın klavyesi çok rahat.",
        "Faturamı ödemeyi unuttum, internetim kesildi.",
    ],
)
def test_off_topic_texts_are_not_auto_approved(engine, thresholds, text: str) -> None:
    assert _relevance(engine, text, "bursa-ulu-camii").score < thresholds["approveFrom"]


def test_monument_text_raises_monument_relevance(engine) -> None:
    own = _relevance(engine, "Yeşil Türbe'nin firuze çinileri ve sandukası", "yesil-turbe")
    other = _relevance(engine, "Yeşil Türbe'nin firuze çinileri ve sandukası", "kursunlu-han")
    assert own.monument > other.monument
