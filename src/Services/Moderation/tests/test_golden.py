"""Gerçek INT8 modelle altın örnekler (Dockerfile export aşaması ve yerelde MODEL_DIR ile).

Eşikler ``calibration.json``'dan okunur; model veya sözlük değişince bant kayması burada yakalanır.
"""

import json
import os
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[1]
MODEL_DIR = os.environ.get("MODEL_DIR")

pytestmark = [
    pytest.mark.model,
    pytest.mark.skipif(not MODEL_DIR or not Path(MODEL_DIR, "model.onnx").exists(), reason="MODEL_DIR yok"),
]


@pytest.fixture(scope="module")
def engine():
    from app.engine import ModerationEngine
    from app.language import LanguageDetector
    from app.model import OnnxToxicityModel

    return ModerationEngine(OnnxToxicityModel(Path(MODEL_DIR), threads=2), LanguageDetector())


@pytest.fixture(scope="module")
def thresholds() -> dict[str, float]:
    return json.loads((ROOT / "calibration.json").read_text(encoding="utf-8"))["thresholds"]


def _score(engine, text: str) -> float:
    return max(engine.classify([text])[0].scores.values())


@pytest.mark.parametrize(
    "text",
    [
        "Süleymaniye Camii'ni ailemle ziyaret ettik, avlusu çok huzurluydu.",
        "Bu camide namaz kılmak çok huzur verdi, Allah kabul etsin.",
        "Türbede dua ettik, ruhları şad olsun.",
        "Şehzade Mustafa'nın burada boğdurulduğunu öğrenince içim burkuldu.",
        "Yahudi mahallesindeki eski sinagogu da gezmenizi tavsiye ederim.",
        "We visited with our kids, the tiles were stunning.",
    ],
)
def test_clean_memories_are_auto_approved(engine, thresholds, text: str) -> None:
    assert _score(engine, text) < thresholds["approveBelow"]


@pytest.mark.parametrize(
    "text",
    [
        "Siktir git lan buradan orospu çocuğu",
        "Amına koyayım böyle restorasyonun.",
        "Buraya gelen herkes aptal, salak turistler.",
        "This place is full of idiots, total garbage.",
        "I will kill you if you come here again.",
    ],
)
def test_clear_abuse_is_auto_rejected(engine, thresholds, text: str) -> None:
    assert _score(engine, text) >= thresholds["rejectFrom"]


@pytest.mark.parametrize(
    "text",
    [
        "Seni gebertirim, bir daha buraya gelme.",
        "Kadınlar mutfağa, camide ne işleri var.",
        "Göt kadar bahçeye bilet istiyorlar.",
    ],
)
def test_borderline_goes_to_review(engine, thresholds, text: str) -> None:
    assert thresholds["approveBelow"] <= _score(engine, text) < thresholds["rejectFrom"]
