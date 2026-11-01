import io

import numpy as np
import pytest
from PIL import Image

from app.references import ReferenceIndex
from app.vision import Preprocess, UnreadableImage, decode
from tests.conftest import jpeg

DINOV2 = Preprocess(256, 224, "bicubic", (0.485, 0.456, 0.406), (0.229, 0.224, 0.225))
VIT = Preprocess(224, None, "bilinear", (0.5, 0.5, 0.5), (0.5, 0.5, 0.5))


def test_decode_reads_jpeg_png_and_webp_as_rgb() -> None:
    for fmt in ("JPEG", "PNG", "WEBP"):
        buffer = io.BytesIO()
        Image.new("RGBA" if fmt != "JPEG" else "RGB", (8, 6), (10, 20, 30)).save(buffer, format=fmt)
        image = decode(buffer.getvalue())
        assert image.mode == "RGB"
        assert image.size == (8, 6)


def test_decode_applies_exif_orientation() -> None:
    buffer = io.BytesIO()
    exif = Image.Exif()
    exif[0x0112] = 6  # 90° saat yönünde döndür
    Image.new("RGB", (40, 20)).save(buffer, format="JPEG", exif=exif)
    assert decode(buffer.getvalue()).size == (20, 40)


@pytest.mark.parametrize("data", [b"", b"not an image", b"GIF89a" + b"\0" * 32])
def test_decode_rejects_unreadable_or_unsupported(data: bytes) -> None:
    with pytest.raises(UnreadableImage):
        decode(data)


def test_decode_rejects_too_many_pixels(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr("app.vision.MAX_PIXELS", 100)
    with pytest.raises(UnreadableImage):
        decode(jpeg((0, 0, 0), (20, 20)))


def test_dinov2_preprocess_scales_short_edge_and_center_crops() -> None:
    # 400x300 → kısa kenar 256 (341x256) → ortadan 224x224
    batch = DINOV2([Image.new("RGB", (400, 300), (255, 255, 255)), Image.new("RGB", (300, 500))])
    assert batch.shape == (2, 3, 224, 224)
    assert batch.dtype == np.float32
    np.testing.assert_allclose(batch[0, :, 0, 0], (1 - np.array(DINOV2.mean)) / np.array(DINOV2.std), rtol=1e-5)


def test_vit_preprocess_resizes_to_square_without_crop() -> None:
    batch = VIT([Image.new("RGB", (640, 480), (0, 0, 0))])
    assert batch.shape == (1, 3, 224, 224)
    assert batch.min() == pytest.approx(-1.0)


def test_preprocess_round_trips_through_json() -> None:
    assert Preprocess.from_json(DINOV2.to_json()) == DINOV2


def test_reference_index_compares_with_the_monument_centroid(references: ReferenceIndex) -> None:
    query = np.asarray([0.1, 0.9, 0.1], dtype=np.float32)
    query /= np.linalg.norm(query)
    match = references.match(query, "yesil-cami")
    assert match.references == 3
    assert 0.97 < match.similarity < 1.0  # merkeze yakın, tek referansla özdeş değil
    single = references.match(query, "tek-gorsel")
    assert single.references == 1
    assert single.similarity < match.similarity


def test_centroid_similarity_does_not_grow_with_reference_count() -> None:
    # Ortogonal referanslar: en yüksek benzerlik referans sayısıyla artardı, merkez benzerliği artmaz.
    rng = np.random.default_rng(3)
    query = rng.normal(size=64).astype(np.float32)
    query /= np.linalg.norm(query)
    scores = []
    for count in (3, 30):
        vectors = rng.normal(size=(count, 64)).astype(np.float32)
        vectors /= np.linalg.norm(vectors, axis=1, keepdims=True)
        scores.append(ReferenceIndex(np.asarray(["x"] * count), vectors, "v").match(query, "x").similarity)
    assert abs(scores[1]) <= abs(scores[0]) + 0.1


def test_reference_index_unknown_monument_has_no_similarity(references: ReferenceIndex) -> None:
    query = np.asarray([1.0, 0.0, 0.0], dtype=np.float32)
    assert references.match(query, "bilinmeyen").similarity is None
    assert references.match(query, None).references == 0


def test_reference_index_round_trips_through_npz(tmp_path, references: ReferenceIndex) -> None:
    keys = np.asarray(["b", "a", "b"])
    vectors = np.eye(3, dtype=np.float32)
    path = tmp_path / "references.npz"
    np.savez(path, keys=keys, vectors=vectors, info=np.asarray('{"version": "v1"}'))
    index = ReferenceIndex.load(path)
    assert (index.info.version, index.info.works, index.info.images) == ("v1", 2, 3)
    # "b" merkezi (e1 + e3) / √2
    assert index.match(np.asarray([0, 0, 1], dtype=np.float32), "b").similarity == pytest.approx(0.7071, abs=1e-4)
    assert index.match(np.asarray([0, 0, 1], dtype=np.float32), "a").similarity == 0.0


def test_catalog_copy_is_max_over_all_reference_images(references: ReferenceIndex) -> None:
    query = np.asarray([0.9, 0.9, 0.9], dtype=np.float32)
    query /= np.linalg.norm(query)
    # Eser "yesil-cami" olsa da kopya tüm indekse bakar: "tek-gorsel"in görseliyle aynı.
    match = references.match(query, "yesil-cami")
    assert match.copy == 1.0
    assert match.similarity < 0.9
    assert references.match(query, "bilinmeyen").copy == 1.0
