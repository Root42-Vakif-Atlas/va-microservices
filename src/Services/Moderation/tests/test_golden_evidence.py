"""Gerçek modellerle Sivil Koruma kanıt sinyalleri (MODEL_DIR: context + vision; katalog kopyası için SEED_DIR).

Eşikler ``calibration-evidence.json``'dan okunur. Örnekler kalibrasyon, hold-out ve prototip setlerinde yoktur.
"""

import io
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
    pytest.mark.skipif(
        not MODEL_DIR or not Path(MODEL_DIR, "context.npz").exists() or not Path(MODEL_DIR, "vision.json").exists(),
        reason="context.npz / vision.json yok",
    ),
]


@pytest.fixture(scope="module")
def thresholds() -> dict[str, float]:
    return json.loads((ROOT / "calibration-evidence.json").read_text(encoding="utf-8"))["thresholds"]


@pytest.fixture(scope="module")
def context():
    from app.context import ContextIndex, OnnxTextEmbedder

    return OnnxTextEmbedder(Path(MODEL_DIR), threads=2), ContextIndex.load(Path(MODEL_DIR, "context.npz"))


@pytest.fixture(scope="module")
def engine():
    from app.engine import ModerationEngine
    from app.references import ReferenceIndex
    from app.vision import OnnxVisionModels

    references = Path(MODEL_DIR, "references.npz")
    if not references.exists():
        pytest.skip("references.npz yok")
    return ModerationEngine(None, None, OnnxVisionModels(Path(MODEL_DIR), threads=2), ReferenceIndex.load(references))


def _damage(context, text: str):
    embedder, index = context
    (signal,) = index.damage(embedder.embed([text]))
    return signal


@pytest.mark.parametrize(
    ("text", "category"),
    [
        (
            "Caminin son cemaat yerindeki mermer sütunlardan biri çatlamış, üstündeki kemer de yarılmış.",
            "structuralDamage",
        ),
        ("Türbenin kapısına ve yan duvarına kırmızı boyayla küfürlü yazılar yazmışlar.", "vandalism"),
        ("Çatıdaki kurşun levhalar sökülmüş, yağmur doğrudan içeri yağıyor.", "theft"),
        ("Gece hamamın odunluğunda çıkan yangın kubbeye kadar ulaşmış.", "fireOrWater"),
    ],
)
def test_damage_descriptions_support_the_report(context, thresholds, text: str, category: str) -> None:
    signal = _damage(context, text)
    assert signal.relevance >= thresholds["damageSupportsFrom"]
    # Tür uyumu yalnızca bilgidir (hold-out'ta doğru tür ilk sırada %63): yalnızca seçilen tür için skor dönmeli.
    assert len(signal.categories) == 6
    assert 0 < signal.categories[category] <= 1


@pytest.mark.parametrize(
    "text",
    [
        "Uygulamayı denemek için yazdım, lütfen silin.",
        "Bugün hava çok güzeldi, parkta yürüyüş yaptık.",
        "abcabcabc abcabcabc abcabcabc",
    ],
)
def test_non_damage_texts_undermine_the_report(context, thresholds, text: str) -> None:
    assert _damage(context, text).relevance < thresholds["damageUnderminesBelow"]


def _jpeg(image: Image.Image, size: int = 640, quality: int = 75) -> bytes:
    image = image.copy()
    image.thumbnail((size, size))
    buffer = io.BytesIO()
    image.save(buffer, format="JPEG", quality=quality)
    return buffer.getvalue()


@pytest.mark.skipif(not SEED_DIR, reason="SEED_DIR yok")
def test_resized_catalog_photo_is_flagged_as_copy(engine, thresholds) -> None:
    original = Image.open(Path(SEED_DIR, "bursa/images/works/bursa-ulu-camii/2.jpg")).convert("RGB")
    (result,) = engine.classify_images([_jpeg(original)], "yesil-turbe")
    assert result.catalog_copy >= thresholds["catalogCopyFrom"]


def test_unrelated_image_is_not_a_copy(engine, thresholds) -> None:
    rng = np.random.default_rng(11)
    noise = Image.fromarray(rng.integers(0, 255, (480, 640, 3), dtype=np.uint8))
    (result,) = engine.classify_images([_jpeg(noise)], "bursa-ulu-camii")
    assert result.catalog_copy < thresholds["catalogCopyFrom"]
