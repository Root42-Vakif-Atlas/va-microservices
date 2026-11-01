"""Görsel moderasyon modelleri (ONNX Runtime, torch yok).

    nsfw.onnx    müstehcenlik sınıflandırıcısı (ViT-base, Falconsai/nsfw_image_detection) → 0–1 olasılık
    dinov2.onnx  DINOv2-small CLS gömmesi (L2 normalize, grafikte) → eser benzerliği için 384 boyutlu vektör

Ön işleme Hugging Face işlemcilerinin numpy/Pillow karşılığıdır; ayarları ``vision.json``'dadır ve
``scripts/export_vision.py`` torch çıktısıyla eşitliği doğrular. Referans indeksi ve sorgu aynı fonksiyonla
işlendiği için gömme uzayı tutarlıdır.
"""

from __future__ import annotations

import io
import json
from dataclasses import dataclass
from pathlib import Path
from typing import TYPE_CHECKING, Protocol

import numpy as np

if TYPE_CHECKING:
    from PIL import Image

# PhotoProcessor.MaxSourcePixels ile aynı (48 MP kamera görüntüsüne yeter); üstü açılmadan reddedilir.
MAX_PIXELS = 50_000_000
FORMATS = frozenset({"JPEG", "PNG", "WEBP"})
_RESAMPLE = {"bilinear": 2, "bicubic": 3}


class UnreadableImage(ValueError):
    """Görsel çözülemedi, desteklenmeyen biçimde veya piksel sınırını aşıyor."""


def decode(data: bytes) -> Image.Image:
    """Baytları RGB görüntüye çevirir (EXIF yönü uygulanır). Piksel sayısı açmadan önce denetlenir."""
    from PIL import Image, ImageOps, UnidentifiedImageError

    try:
        with Image.open(io.BytesIO(data)) as image:
            if image.format not in FORMATS or image.width * image.height > MAX_PIXELS:
                raise UnreadableImage(f"{image.format} {image.width}x{image.height}")
            return ImageOps.exif_transpose(image).convert("RGB")
    except (UnidentifiedImageError, OSError, Image.DecompressionBombError) as exc:
        raise UnreadableImage(type(exc).__name__) from exc


@dataclass(frozen=True)
class Preprocess:
    """Yeniden boyutlandırma (+ isteğe bağlı orta kırpma) ve normalizasyon → NCHW float32.

    ``crop`` varsa kısa kenar ``size``'a ölçeklenir ve ortadan ``crop`` kare kesilir (DINOv2); yoksa
    görüntü doğrudan ``size`` kareye getirilir (ViT).
    """

    size: int
    crop: int | None
    resample: str
    mean: tuple[float, float, float]
    std: tuple[float, float, float]

    @classmethod
    def from_json(cls, spec: dict) -> Preprocess:
        return cls(spec["size"], spec.get("crop"), spec["resample"], tuple(spec["mean"]), tuple(spec["std"]))

    def to_json(self) -> dict:
        return {"size": self.size, "crop": self.crop, "resample": self.resample, "mean": self.mean, "std": self.std}

    def __call__(self, images: list[Image.Image]) -> np.ndarray:
        mean = np.asarray(self.mean, dtype=np.float32)
        std = np.asarray(self.std, dtype=np.float32)
        return np.stack([((self._resize(image) / np.float32(255) - mean) / std).transpose(2, 0, 1) for image in images])

    def _resize(self, image: Image.Image) -> np.ndarray:
        resample = _RESAMPLE[self.resample]
        if self.crop is None:
            return np.asarray(image.resize((self.size, self.size), resample), dtype=np.float32)
        # transformers get_resize_output_image_size: kısa kenar = size, uzun kenar tamsayıya kesilir.
        width, height = image.size
        if width <= height:
            target = (self.size, int(self.size * height / width))
        else:
            target = (int(self.size * width / height), self.size)
        resized = image.resize(target, resample)
        left, top = (resized.width - self.crop) // 2, (resized.height - self.crop) // 2
        return np.asarray(resized.crop((left, top, left + self.crop, top + self.crop)), dtype=np.float32)


@dataclass(frozen=True)
class VisionInfo:
    name: str
    version: str
    nsfw_model: str
    embedding_model: str


class VisionModels(Protocol):
    info: VisionInfo

    def nsfw(self, images: list[Image.Image]) -> list[float]: ...

    def embed(self, images: list[Image.Image]) -> np.ndarray: ...


class OnnxVisionModels:
    """İki ORT oturumu; ``nsfw`` ve ``embed`` iş parçacığı güvenlidir."""

    def __init__(self, model_dir: Path, threads: int = 1) -> None:
        import onnxruntime as ort

        spec = json.loads((model_dir / "vision.json").read_text(encoding="utf-8"))
        nsfw, embedding = spec["nsfw"], spec["embedding"]
        self.info = VisionInfo(spec["name"], spec["version"], nsfw["id"], embedding["id"])
        self._nsfw_preprocess = Preprocess.from_json(nsfw["preprocess"])
        self._nsfw_index = nsfw["labels"].index("nsfw")
        self._embed_preprocess = Preprocess.from_json(embedding["preprocess"])

        options = ort.SessionOptions()
        options.intra_op_num_threads = threads
        options.inter_op_num_threads = 1
        options.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
        options.enable_cpu_mem_arena = False
        providers = ["CPUExecutionProvider"]
        self._nsfw = ort.InferenceSession(str(model_dir / nsfw["file"]), options, providers=providers)
        self._embedding = ort.InferenceSession(str(model_dir / embedding["file"]), options, providers=providers)

    def nsfw(self, images: list[Image.Image]) -> list[float]:
        (probabilities,) = self._nsfw.run(["probabilities"], {"pixel_values": self._nsfw_preprocess(images)})
        return [round(float(row[self._nsfw_index]), 4) for row in probabilities]

    def embed(self, images: list[Image.Image]) -> np.ndarray:
        (vectors,) = self._embedding.run(["embedding"], {"pixel_values": self._embed_preprocess(images)})
        return vectors

    def warm_up(self) -> None:
        from PIL import Image

        sample = [Image.new("RGB", (320, 240), (180, 160, 120))]
        self.nsfw(sample)
        self.embed(sample)
