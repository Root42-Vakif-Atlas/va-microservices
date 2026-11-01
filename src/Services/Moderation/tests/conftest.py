import io
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import numpy as np
import pytest
from PIL import Image

from app.context import ContextIndex, ContextModelInfo
from app.references import ReferenceIndex
from app.vision import VisionInfo


def jpeg(color: tuple[int, int, int], size: tuple[int, int] = (64, 48)) -> bytes:
    buffer = io.BytesIO()
    Image.new("RGB", size, color).save(buffer, format="JPEG")
    return buffer.getvalue()


class FakeVision:
    """Gerçek modeller yerine: kırmızı baskın görsel "müstehcen", gömme = normalize ortalama renk."""

    info = VisionInfo("vakifatlas-vision", "test", "nsfw-test", "embedding-test")

    def __init__(self) -> None:
        self.calls = 0

    def nsfw(self, images: list[Image.Image]) -> list[float]:
        self.calls += 1
        return [0.97 if np.asarray(image).mean(axis=(0, 1))[0] > 200 else 0.01 for image in images]

    def embed(self, images: list[Image.Image]) -> np.ndarray:
        vectors = np.stack([np.asarray(image, dtype=np.float32).mean(axis=(0, 1)) for image in images])
        return vectors / np.linalg.norm(vectors, axis=1, keepdims=True)


@pytest.fixture
def vision() -> FakeVision:
    return FakeVision()


@pytest.fixture
def references() -> ReferenceIndex:
    # "yesil-cami": yeşilimsi üç referans; "tek-gorsel": tek referans (karar katmanı en az 3 ister).
    vectors = np.asarray([[0.1, 0.9, 0.1], [0.2, 0.8, 0.2], [0.1, 1.0, 0.3], [0.9, 0.9, 0.9]], dtype=np.float32)
    vectors /= np.linalg.norm(vectors, axis=1, keepdims=True)
    return ReferenceIndex(np.asarray(["yesil-cami"] * 3 + ["tek-gorsel"]), vectors, "test-refs")


class FakeEmbedder:
    """Gerçek model yerine: "cami"/"türbe" geçen metin anı yönünde, "çini" geçen eser yönünde, diğerleri ilgisiz."""

    info = ContextModelInfo("vakifatlas-context", "test", "embedding-test")

    def embed(self, texts: list[str]) -> np.ndarray:
        def vector(text: str) -> list[float]:
            lowered = text.lower()
            if "çini" in lowered:
                return [0.0, 1.0, 0.0]
            if "cami" in lowered or "türbe" in lowered:
                return [1.0, 0.0, 0.0]
            return [0.0, 0.0, 1.0]

        return np.asarray([vector(t) for t in texts], dtype=np.float32)


@pytest.fixture
def embedder() -> FakeEmbedder:
    return FakeEmbedder()


@pytest.fixture
def context() -> ContextIndex:
    # Prototipler anı yönünde; "yesil-cami"nin iki metin parçası çini yönünde; hasar prototipleri: "theft" çini
    # yönünde, "vandalism" ilgisiz yönde.
    chunks = np.asarray([[0.0, 1.0, 0.0], [0.0, 1.0, 0.0]], dtype=np.float32)
    prototypes = np.asarray([[1.0, 0.0, 0.0]] * 5, dtype=np.float32)
    damage = np.asarray([[0.0, 1.0, 0.0]] * 3 + [[0.0, 0.0, 1.0]] * 3, dtype=np.float32)
    labels = np.asarray(["theft"] * 3 + ["vandalism"] * 3)
    return ContextIndex(np.asarray(["yesil-cami"] * 2), chunks, prototypes, "test-context", damage, labels)
