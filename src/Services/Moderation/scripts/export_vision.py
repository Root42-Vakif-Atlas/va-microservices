"""Görsel modelleri ONNX'e çevirir, NSFW modelini INT8'e kuantize eder ve torch ile eşitliği doğrular.

    python scripts/export_vision.py --out models/ [--check-images <klasör>]

Çıktılar (``--out`` altında):
    nsfw.onnx      Falconsai/nsfw_image_detection (ViT-base, 224) → [normal, nsfw] olasılıkları, INT8
    dinov2.onnx    facebook/dinov2-small → L2 normalize CLS gömmesi (384), FP32
    vision.json    model kimlikleri, sabit revizyonlar, ön işleme ayarları, dosya özetleri

Revizyonlar sabittir (tekrarlanabilir derleme). Eşitlik denetimi: Hugging Face işlemcisi + torch çıktısı ile
``app.vision`` ön işlemesi + ORT çıktısı karşılaştırılır; NSFW olasılık farkı ≤ 0,03, gömme kosinüsü ≥ 0,995.
Yalnızca dışa aktarımda torch/transformers gerekir; servis çalışırken gerekmez.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from app.vision import OnnxVisionModels, Preprocess, decode

VISION_NAME = "vakifatlas-vision"
VISION_VERSION = "1.0"
NSFW_ID, NSFW_REVISION = "Falconsai/nsfw_image_detection", "96cb0d0342c7afb80cab76ecc58b265fa44da256"
EMBEDDING_ID, EMBEDDING_REVISION = "facebook/dinov2-small", "ed25f3a31f01632728cabb09d1542f84ab7b0056"
NSFW_PREPROCESS = Preprocess(224, None, "bilinear", (0.5, 0.5, 0.5), (0.5, 0.5, 0.5))
EMBEDDING_PREPROCESS = Preprocess(256, 224, "bicubic", (0.485, 0.456, 0.406), (0.229, 0.224, 0.225))
MAX_NSFW_DIFF = 0.03
MIN_COSINE = 0.995


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as file:
        for block in iter(lambda: file.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--check-images", type=Path, help="eşitlik denetimi için görseller (varsayılan: sentetik)")
    args = parser.parse_args()
    args.out.mkdir(parents=True, exist_ok=True)

    import torch
    from transformers import AutoModel, AutoModelForImageClassification

    class Probabilities(torch.nn.Module):
        def __init__(self, inner: torch.nn.Module) -> None:
            super().__init__()
            self.inner = inner

        def forward(self, pixel_values: torch.Tensor) -> torch.Tensor:
            return torch.softmax(self.inner(pixel_values=pixel_values).logits, dim=-1)

    class Embedding(torch.nn.Module):
        def __init__(self, inner: torch.nn.Module) -> None:
            super().__init__()
            self.inner = inner

        def forward(self, pixel_values: torch.Tensor) -> torch.Tensor:
            cls = self.inner(pixel_values=pixel_values).last_hidden_state[:, 0, :]
            return torch.nn.functional.normalize(cls, dim=-1)

    nsfw = AutoModelForImageClassification.from_pretrained(NSFW_ID, revision=NSFW_REVISION, use_safetensors=True).eval()
    labels = [nsfw.config.id2label[i] for i in range(len(nsfw.config.id2label))]
    embedding = AutoModel.from_pretrained(EMBEDDING_ID, revision=EMBEDDING_REVISION, use_safetensors=True).eval()

    sample = torch.zeros(2, 3, 224, 224)
    export(Probabilities(nsfw), sample, args.out / "nsfw.fp32.onnx", "probabilities")
    export(Embedding(embedding), sample, args.out / "dinov2.onnx", "embedding")

    from onnxruntime.quantization import QuantType, quantize_dynamic

    # ViT'de dinamik INT8 (MatMul, kanal başına): 343 MB → ~88 MB; olasılık farkı aşağıda denetlenir.
    # DINOv2 FP32 kalır: 88 MB ve gömme uzayı (referans indeksi) kuantizasyon gürültüsünden etkilenmez.
    quantize_dynamic(
        model_input=str(args.out / "nsfw.fp32.onnx"),
        model_output=str(args.out / "nsfw.onnx"),
        weight_type=QuantType.QInt8,
        per_channel=True,
        op_types_to_quantize=["MatMul"],
    )

    spec = {
        "name": VISION_NAME,
        "version": VISION_VERSION,
        "nsfw": {
            "id": NSFW_ID,
            "revision": NSFW_REVISION,
            "file": "nsfw.onnx",
            "sha256": sha256(args.out / "nsfw.onnx"),
            "labels": labels,
            "preprocess": NSFW_PREPROCESS.to_json(),
        },
        "embedding": {
            "id": EMBEDDING_ID,
            "revision": EMBEDDING_REVISION,
            "file": "dinov2.onnx",
            "sha256": sha256(args.out / "dinov2.onnx"),
            "dimensions": embedding.config.hidden_size,
            "pooling": "cls_l2",
            "preprocess": EMBEDDING_PREPROCESS.to_json(),
        },
    }
    (args.out / "vision.json").write_text(json.dumps(spec, indent=2) + "\n", encoding="utf-8")

    check_parity(args.out, nsfw, embedding, sample_images(args.check_images))
    for name in ("nsfw.fp32.onnx", "nsfw.onnx", "dinov2.onnx"):
        print(f"{name}: {(args.out / name).stat().st_size / 1e6:.0f} MB")


def export(module, sample, path: Path, output: str) -> None:
    import torch

    torch.onnx.export(
        module,
        (sample,),
        str(path),
        input_names=["pixel_values"],
        output_names=[output],
        dynamic_axes={"pixel_values": {0: "batch"}, output: {0: "batch"}},
        opset_version=17,
        dynamo=False,
    )


def sample_images(folder: Path | None) -> list:
    if folder:
        paths = sorted(p for p in folder.rglob("*") if p.suffix.lower() in {".jpg", ".jpeg", ".png", ".webp"})[:16]
        return [decode(p.read_bytes()) for p in paths]
    from PIL import Image

    rng = __import__("numpy").random.default_rng(42)
    return [Image.fromarray(rng.integers(0, 255, (300 + 40 * i, 420, 3), dtype="uint8")) for i in range(4)]


def check_parity(out: Path, nsfw, embedding, images: list) -> None:
    """HF işlemcisi + torch ile app.vision + ORT aynı sonucu vermeli (ön işleme ve dışa aktarım doğrulaması)."""
    import numpy as np
    import torch
    from transformers import AutoImageProcessor

    nsfw_processor = AutoImageProcessor.from_pretrained(NSFW_ID, revision=NSFW_REVISION, use_fast=False)
    embedding_processor = AutoImageProcessor.from_pretrained(EMBEDDING_ID, revision=EMBEDDING_REVISION, use_fast=False)
    with torch.inference_mode():
        nsfw_index = [nsfw.config.id2label[i] for i in range(len(nsfw.config.id2label))].index("nsfw")
        logits = nsfw(**nsfw_processor(images=images, return_tensors="pt")).logits
        expected_nsfw = torch.softmax(logits, -1)[:, nsfw_index]
        cls = embedding(**embedding_processor(images=images, return_tensors="pt")).last_hidden_state[:, 0, :]
        expected_vectors = torch.nn.functional.normalize(cls, dim=-1).numpy()

    models = OnnxVisionModels(out, threads=4)
    nsfw_diff = float(np.max(np.abs(np.asarray(models.nsfw(images)) - expected_nsfw.numpy())))
    cosine = float(np.min(np.sum(models.embed(images) * expected_vectors, axis=1)))
    print(f"parity: {len(images)} images, max NSFW diff {nsfw_diff:.4f}, min embedding cosine {cosine:.5f}")
    if nsfw_diff > MAX_NSFW_DIFF or cosine < MIN_COSINE:
        raise SystemExit("ONNX output does not match torch (preprocessing or export changed)")


if __name__ == "__main__":
    main()
