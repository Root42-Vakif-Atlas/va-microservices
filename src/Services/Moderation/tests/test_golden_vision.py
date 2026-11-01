"""Gerçek görsel modellerle altın örnekler (MODEL_DIR'de vision.json; seed görselleri için SEED_DIR).

Eşikler ``calibration-vision.json``'dan okunur; model, ön işleme veya indeks değişince bant kayması burada
yakalanır.
"""

import json
import os
from pathlib import Path

import numpy as np
import pytest
from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
MODEL_DIR = os.environ.get("MODEL_DIR")
SEED_DIR = os.environ.get("SEED_DIR")

pytestmark = [
    pytest.mark.model,
    pytest.mark.skipif(not MODEL_DIR or not Path(MODEL_DIR, "vision.json").exists(), reason="vision.json yok"),
]


@pytest.fixture(scope="module")
def engine():
    from app.engine import ModerationEngine
    from app.references import ReferenceIndex
    from app.vision import OnnxVisionModels

    references = Path(MODEL_DIR, "references.npz")
    return ModerationEngine(
        None,
        None,
        OnnxVisionModels(Path(MODEL_DIR), threads=2),
        ReferenceIndex.load(references) if references.exists() else None,
    )


@pytest.fixture(scope="module")
def thresholds() -> dict[str, float]:
    return json.loads((ROOT / "calibration-vision.json").read_text(encoding="utf-8"))["thresholds"]


def _png(image: Image.Image) -> bytes:
    import io

    buffer = io.BytesIO()
    image.save(buffer, format="PNG")
    return buffer.getvalue()


def test_plain_and_noise_images_are_safe(engine, thresholds) -> None:
    rng = np.random.default_rng(7)
    images = [
        _png(Image.new("RGB", (640, 480), (200, 190, 170))),
        _png(Image.fromarray(rng.integers(0, 255, (480, 640, 3), dtype=np.uint8))),
    ]
    for result in engine.classify_images(images, None):
        assert result.readable
        assert result.nsfw < thresholds["nsfwReviewFrom"]
        assert result.similarity is None


def test_noise_is_not_similar_to_a_monument(engine, thresholds) -> None:
    if engine.references is None:
        pytest.skip("references.npz yok")
    rng = np.random.default_rng(7)
    noise = _png(Image.fromarray(rng.integers(0, 255, (480, 640, 3), dtype=np.uint8)))
    (result,) = engine.classify_images([noise], "bursa-ulu-camii")
    assert result.references >= thresholds["similarityMinReferences"]
    assert result.similarity < thresholds["similarityReviewBelow"]


@pytest.mark.skipif(not SEED_DIR, reason="SEED_DIR yok")
def test_catalog_photo_matches_its_monument_and_is_safe(engine, thresholds) -> None:
    if engine.references is None:
        pytest.skip("references.npz yok")
    photo = Path(SEED_DIR, "bursa/images/works/bursa-ulu-camii/1.jpg").read_bytes()
    (result,) = engine.classify_images([photo], "bursa-ulu-camii")
    assert result.nsfw < 0.01
    assert result.similarity >= thresholds["similarityReviewBelow"]
