"""Bağlam modelini (çok dilli cümle gömmesi) ONNX'e çevirir, INT8'e kuantize eder ve torch ile eşitliği doğrular.

    python scripts/export_context.py --out models/

Çıktılar (``--out`` altında):
    context.onnx             sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2 → ortalama havuzlama +
                             L2 normalize (384), INT8 (MatMul + Gather)
    context_tokenizer.json   modelin tokenizer'ı
    context.json             model kimliği, sabit revizyon, dosya özetleri

Revizyon sabittir (tekrarlanabilir derleme). Eşitlik: sentence-transformers'ın havuzlaması (dikkat maskeli ortalama)
torch'ta yeniden kurulur ve ``app.context.OnnxTextEmbedder`` çıktısıyla karşılaştırılır; kosinüs ≥ 0,99.
Model seçimi (multilingual-e5-small'a karşı) ve eşikler: README → Bağlam modeli, ``calibration-context.json``.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from app.context import MAX_TOKENS, OnnxTextEmbedder

CONTEXT_NAME = "vakifatlas-context"
CONTEXT_VERSION = "1.0"
EMBEDDING_ID = "sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2"
EMBEDDING_REVISION = "e8f8c211226b894fcb81acc59f3b34ba3efd5f42"
MIN_COSINE = 0.99
SAMPLES = [
    "Süleymaniye Camii'ni ailemle ziyaret ettik, avlusu çok huzurluydu.",
    "Türbede dua ettik, ruhları şad olsun.",
    "En uygun kredi faizleri için hemen arayın.",
    "Galatasaray yine kazandı, bu sene şampiyonluk bizim!",
    "We visited with our kids, the tiles were stunning.",
    "Kervansarayın taç kapısı, Selçuklu taş işçiliğinin en güzel örneklerinden biri. " * 6,
    "asdasdasd",
    "Мы посетили мечеть, внутри было очень спокойно.",
]


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as file:
        for block in iter(lambda: file.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    args.out.mkdir(parents=True, exist_ok=True)

    import torch
    from transformers import AutoModel, AutoTokenizer

    class Embedding(torch.nn.Module):
        def __init__(self, inner: torch.nn.Module) -> None:
            super().__init__()
            self.inner = inner

        def forward(self, input_ids: torch.Tensor, attention_mask: torch.Tensor) -> torch.Tensor:
            hidden = self.inner(input_ids=input_ids, attention_mask=attention_mask).last_hidden_state
            mask = attention_mask.unsqueeze(-1).to(hidden.dtype)
            mean = (hidden * mask).sum(1) / mask.sum(1).clamp(min=1e-9)
            return torch.nn.functional.normalize(mean, dim=-1)

    tokenizer = AutoTokenizer.from_pretrained(EMBEDDING_ID, revision=EMBEDDING_REVISION)
    model = AutoModel.from_pretrained(EMBEDDING_ID, revision=EMBEDDING_REVISION, use_safetensors=True).eval()
    tokenizer.backend_tokenizer.save(str(args.out / "context_tokenizer.json"))
    # Referans dışa aktarımdan önce: torch.onnx.export modelin durumunu değiştiriyor, sonradan hesaplanan torch
    # çıktısı kayıyor (doğru ONNX dosyası 0,90–0,94 "eşitlik" alıyordu).
    expected = reference(tokenizer, model)

    sample = tokenizer(SAMPLES[:2], padding=True, return_tensors="pt")
    fp32 = args.out / "context.fp32.onnx"
    torch.onnx.export(
        Embedding(model),
        (sample["input_ids"], sample["attention_mask"]),
        str(fp32),
        input_names=["input_ids", "attention_mask"],
        output_names=["embedding"],
        dynamic_axes={
            "input_ids": {0: "batch", 1: "sequence"},
            "attention_mask": {0: "batch", 1: "sequence"},
            "embedding": {0: "batch"},
        },
        opset_version=17,
        dynamo=False,
    )

    from onnxruntime.quantization import QuantType, quantize_dynamic

    # Ağırlıkların ~%80'i 250k × 384 sözcük gömmesi (Gather); MatMul + Gather INT8: ~470 MB → ~120 MB
    # (kosinüs ≥ 0,997, aşağıda denetlenir).
    quantize_dynamic(
        model_input=str(fp32),
        model_output=str(args.out / "context.onnx"),
        weight_type=QuantType.QInt8,
        per_channel=True,
        op_types_to_quantize=["MatMul", "Gather"],
    )

    spec = {
        "name": CONTEXT_NAME,
        "version": CONTEXT_VERSION,
        "embedding": {
            "id": EMBEDDING_ID,
            "revision": EMBEDDING_REVISION,
            "file": "context.onnx",
            "sha256": sha256(args.out / "context.onnx"),
            "tokenizer": "context_tokenizer.json",
            "dimensions": model.config.hidden_size,
            "pooling": "mean_l2",
            "maxTokens": MAX_TOKENS,
        },
    }
    (args.out / "context.json").write_text(json.dumps(spec, indent=2) + "\n", encoding="utf-8")

    check_parity(args.out, expected)
    for name in ("context.fp32.onnx", "context.onnx"):
        print(f"{name}: {(args.out / name).stat().st_size / 1e6:.0f} MB")


def reference(tokenizer, model):
    """HF tokenizer + sentence-transformers havuzlaması (dikkat maskeli ortalama, L2) torch'ta."""
    import torch

    with torch.inference_mode():
        batch = tokenizer(SAMPLES, padding=True, truncation=True, max_length=MAX_TOKENS, return_tensors="pt")
        hidden = model(**batch).last_hidden_state
        mask = batch["attention_mask"].unsqueeze(-1).float()
        return torch.nn.functional.normalize((hidden * mask).sum(1) / mask.sum(1), dim=-1).numpy()


def check_parity(out: Path, expected) -> None:
    """app.context + ORT (INT8) torch referansıyla aynı yönü vermeli."""
    import numpy as np

    actual = OnnxTextEmbedder(out, threads=4).embed(SAMPLES)
    cosine = float(np.min(np.sum(actual * expected, axis=1)))
    print(f"parity: {len(SAMPLES)} texts, min cosine INT8 vs torch {cosine:.5f}")
    if cosine < MIN_COSINE:
        raise SystemExit("ONNX output does not match torch (tokenizer, pooling or quantization changed)")


if __name__ == "__main__":
    main()
