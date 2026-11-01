"""ONNX Runtime ile zehirlilik skorlaması (Detoxify çok dilli, XLM-R base, INT8)."""

from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path
from typing import Protocol

import numpy as np

MAX_TOKENS = 512


@dataclass(frozen=True)
class ModelInfo:
    name: str
    version: str
    categories: tuple[str, ...]


class Scorer(Protocol):
    info: ModelInfo

    def score(self, texts: list[str]) -> list[dict[str, float]]: ...


class OnnxToxicityModel:
    """Tokenizer + ORT oturumu. ``score`` iş parçacığı güvenlidir (ORT ``run`` eşzamanlı çağrılabilir)."""

    def __init__(self, model_dir: Path, threads: int = 1) -> None:
        import onnxruntime as ort
        from tokenizers import Tokenizer

        labels = json.loads((model_dir / "labels.json").read_text(encoding="utf-8"))
        self.info = ModelInfo(labels["name"], labels["version"], tuple(labels["categories"]))

        self._tokenizer = Tokenizer.from_file(str(model_dir / "tokenizer.json"))
        self._tokenizer.enable_truncation(MAX_TOKENS)
        self._tokenizer.enable_padding(pad_id=self._tokenizer.token_to_id("<pad>"), pad_token="<pad>")

        options = ort.SessionOptions()
        options.intra_op_num_threads = threads
        options.inter_op_num_threads = 1
        options.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
        # Arena kapalı: değişken uzunluklu girdide bellek büyümez (pod limiti sabit kalır).
        options.enable_cpu_mem_arena = False
        self._session = ort.InferenceSession(str(model_dir / "model.onnx"), options, providers=["CPUExecutionProvider"])

    def score(self, texts: list[str]) -> list[dict[str, float]]:
        encodings = self._tokenizer.encode_batch(texts)
        input_ids = np.asarray([e.ids for e in encodings], dtype=np.int64)
        attention_mask = np.asarray([e.attention_mask for e in encodings], dtype=np.int64)
        (scores,) = self._session.run(["scores"], {"input_ids": input_ids, "attention_mask": attention_mask})
        return [
            {name: round(float(value), 4) for name, value in zip(self.info.categories, row, strict=True)}
            for row in scores
        ]

    def warm_up(self) -> None:
        self.score(["Merhaba", "Hello"])
