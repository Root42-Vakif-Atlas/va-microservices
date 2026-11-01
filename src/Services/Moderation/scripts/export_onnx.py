"""Detoxify çok dilli checkpoint'ini ONNX'e çevirir ve INT8'e kuantize eder (ADR 58).

    python scripts/export_onnx.py --checkpoint multilingual_debiased-0b549669.ckpt --out models/

Çıktılar (``--out`` altında):
    model.fp32.onnx  karşılaştırma/kalibrasyon için tam hassasiyet
    model.onnx       servisin yüklediği INT8 model (MatMul + Gather dinamik kuantize)
    tokenizer.json   XLM-R fast tokenizer
    labels.json      kategori adları + model kimliği

Yalnızca dışa aktarımda torch/transformers gerekir; servis çalışırken gerekmez.
"""

from __future__ import annotations

import argparse
import gc
import hashlib
import json
import subprocess
import sys
from pathlib import Path

CHECKPOINT_SHA256 = "0b5496691403977207f24778695389494a412a76c2cb351e99f024f2a4e3f3ff"
BASE_MODEL = "FacebookAI/xlm-roberta-base"
BASE_REVISION = "e73636d4f797dec63c3081bb6ed5c7b0bb3f2089"  # 2024-02-19, tekrarlanabilir derleme
MODEL_NAME = "detoxify-multilingual"
MODEL_VERSION = "0.4-int8"
# Checkpoint 16 çıktı taşır: ilk 7'si moderasyon kategorileri, kalanı kimlik alt grupları
# (Detoxify de yalnızca ilk 7'yi döndürür).
CATEGORY_COUNT = 7


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as file:
        for block in iter(lambda: file.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--checkpoint", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--skip-hash", action="store_true")
    parser.add_argument("--quantize-only", action="store_true", help=argparse.SUPPRESS)
    args = parser.parse_args()

    if args.quantize_only:
        quantize(args.out)
        return

    if not args.skip_hash and sha256(args.checkpoint) != CHECKPOINT_SHA256:
        raise SystemExit("checkpoint SHA-256 mismatch")

    import torch
    from transformers import AutoConfig, AutoTokenizer, XLMRobertaForSequenceClassification

    loaded = torch.load(args.checkpoint, map_location="cpu", weights_only=False)
    config = loaded["config"]
    classes = config["dataset"]["args"]["classes"][:CATEGORY_COUNT]
    num_labels = config["arch"]["args"]["num_classes"]

    hf_config = AutoConfig.from_pretrained(BASE_MODEL, revision=BASE_REVISION, num_labels=num_labels)
    model = XLMRobertaForSequenceClassification(hf_config)
    state = {k: v for k, v in loaded["state_dict"].items() if not k.endswith("position_ids")}
    missing, unexpected = model.load_state_dict(state, strict=False)
    if missing or unexpected:
        raise SystemExit(f"state dict mismatch: missing={missing} unexpected={unexpected}")
    # Checkpoint ağırlıkları modele kopyalandı; ikinci kopya bellekte tutulmaz.
    del loaded, state
    gc.collect()
    model.eval()

    class Scores(torch.nn.Module):
        """Sigmoid grafikte: servis olasılıkları doğrudan okur."""

        def __init__(self, inner: torch.nn.Module) -> None:
            super().__init__()
            self.inner = inner

        def forward(self, input_ids: torch.Tensor, attention_mask: torch.Tensor) -> torch.Tensor:
            logits = self.inner(input_ids=input_ids, attention_mask=attention_mask).logits
            return torch.sigmoid(logits[:, :CATEGORY_COUNT])

    args.out.mkdir(parents=True, exist_ok=True)
    tokenizer = AutoTokenizer.from_pretrained(BASE_MODEL, revision=BASE_REVISION)
    tokenizer.backend_tokenizer.save(str(args.out / "tokenizer.json"))

    sample = tokenizer(["Süleymaniye'yi ziyaret ettik.", "harika"], padding=True, return_tensors="pt")
    fp32 = args.out / "model.fp32.onnx"
    torch.onnx.export(
        Scores(model),
        (sample["input_ids"], sample["attention_mask"]),
        str(fp32),
        input_names=["input_ids", "attention_mask"],
        output_names=["scores"],
        dynamic_axes={
            "input_ids": {0: "batch", 1: "sequence"},
            "attention_mask": {0: "batch", 1: "sequence"},
            "scores": {0: "batch"},
        },
        opset_version=17,
        dynamo=False,
    )

    del model, sample
    gc.collect()
    # Kuantizasyon torch'suz ayrı süreçte: tepe bellek ~6,7 GB yerine adım başına ~3 GB (8 GB'lık derleme VM'i).
    subprocess.run(
        [sys.executable, __file__, "--checkpoint", "-", "--out", str(args.out), "--quantize-only"], check=True
    )

    (args.out / "labels.json").write_text(
        json.dumps({"name": MODEL_NAME, "version": MODEL_VERSION, "categories": classes}, indent=2) + "\n",
        encoding="utf-8",
    )
    for name in ("model.fp32.onnx", "model.onnx", "tokenizer.json"):
        print(f"{name}: {(args.out / name).stat().st_size / 1e6:.0f} MB")


def quantize(out: Path) -> None:
    from onnxruntime.quantization import QuantType, quantize_dynamic

    quantize_dynamic(
        model_input=str(out / "model.fp32.onnx"),
        model_output=str(out / "model.onnx"),
        weight_type=QuantType.QInt8,
        per_channel=True,
        op_types_to_quantize=["MatMul", "Gather"],
    )


if __name__ == "__main__":
    main()
