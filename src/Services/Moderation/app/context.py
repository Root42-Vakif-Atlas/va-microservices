"""Anının eserle bağlamı: çok dilli cümle gömmesi + bağlam indeksi (ONNX Runtime, torch yok).

    context.onnx            sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2 → ortalama havuzlama,
                            L2 normalize (grafikte), 384 boyut, INT8
    context_tokenizer.json  modelin kendi tokenizer'ı (Detoxify'ınkinden ayrı)
    context.json            model kimliği, sabit revizyon, dosya özeti
    context.npz             ``scripts/build_context.py``: eser merkezleri + anı türü prototipleri + hasar türü
                            prototipleri (Sivil Koruma bildirimlerinin kanıt değerlendirmesi)

Bağlam iki benzerliğin büyüğüdür:
    eser     anının, eserin metin parçalarının (ad, takma ad, il, külliye, bölüm açıklamaları) normalize merkezine
             kosinüsü; eser indekste yoksa ``None``
    tür      anının, "bir vakıf eserini ziyaret anısı" prototiplerine (``data/context_prototypes.txt``) en yakın
             beşinin ortalama kosinüsü (tek prototiple sözcük örtüşmesi — "akşam yemeği" ↔ "imarette yemek" —
             skoru şişirmez; hold-out setinde k=3'te konu dışı kaçak %15, k=5'te %5)

Kişisel anıların çoğu eserin adını anmaz ("Babamla son kez geldiğimiz yerdi"); yalnızca esere benzerlik bunları
ilgisiz sayardı, tür benzerliği yakalar. Tersine eserin adını anan ama konusu başka olan metin (reklam) yüksek
bağlam alabilir: spam ayrı sinyaldir (``app.spam``) ve karar katmanında bağlamdan önce uygulanır. Skor olasılık
değildir; eşikler ``calibration-context.json``'dadır.

Konu dışı karşı prototipler (bağlam − konu dışı benzerliği) denendi ve bırakıldı: kalibrasyon setinde AUC'yi
artırdı (0,94 → 0,97) ama yeni konulardaki hold-out setinde konu dışı kaçağı %15'ten %25–30'a çıkardı (ezber).
"""

from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path
from typing import Protocol

import numpy as np

from app.references import centroid

MAX_TOKENS = 256
# Tür benzerliği: en yakın k prototipin ortalaması (tek prototipe tesadüfi yakınlık skoru şişirmez).
TOP_K = 5
# Hasar türü uyumu: türün prototiplerine en yakın k'nin ortalaması (türde 8 prototip var).
CATEGORY_TOP_K = 3


@dataclass(frozen=True)
class ContextModelInfo:
    name: str
    version: str
    embedding_model: str


class TextEmbedder(Protocol):
    info: ContextModelInfo

    def embed(self, texts: list[str]) -> np.ndarray: ...


class OnnxTextEmbedder:
    """Tokenizer + ORT oturumu. ``embed`` iş parçacığı güvenlidir."""

    def __init__(self, model_dir: Path, threads: int = 1) -> None:
        import onnxruntime as ort
        from tokenizers import Tokenizer

        spec = json.loads((model_dir / "context.json").read_text(encoding="utf-8"))
        self.info = ContextModelInfo(spec["name"], spec["version"], spec["embedding"]["id"])

        self._tokenizer = Tokenizer.from_file(str(model_dir / spec["embedding"]["tokenizer"]))
        self._tokenizer.enable_truncation(MAX_TOKENS)
        self._tokenizer.enable_padding(pad_id=self._tokenizer.token_to_id("<pad>"), pad_token="<pad>")

        options = ort.SessionOptions()
        options.intra_op_num_threads = threads
        options.inter_op_num_threads = 1
        options.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
        options.enable_cpu_mem_arena = False
        self._session = ort.InferenceSession(
            str(model_dir / spec["embedding"]["file"]), options, providers=["CPUExecutionProvider"]
        )

    def embed(self, texts: list[str]) -> np.ndarray:
        encodings = self._tokenizer.encode_batch(texts)
        input_ids = np.asarray([e.ids for e in encodings], dtype=np.int64)
        attention_mask = np.asarray([e.attention_mask for e in encodings], dtype=np.int64)
        (vectors,) = self._session.run(["embedding"], {"input_ids": input_ids, "attention_mask": attention_mask})
        return vectors

    def warm_up(self) -> None:
        self.embed(["Merhaba", "Hello"])


@dataclass(frozen=True)
class ContextIndexInfo:
    version: str
    works: int
    prototypes: int
    damage_prototypes: int


@dataclass(frozen=True)
class DamageSignal:
    """Hasar bildirimi açıklaması: hasar tarifine benzerlik (en yakın 5 hasar prototipi) ve tür başına uyum
    (türün en yakın 3 prototipi). Tür uyumu yalnızca bilgidir: hold-out'ta doğru tür ilk sırada %63."""

    relevance: float
    categories: dict[str, float]


@dataclass(frozen=True)
class Relevance:
    # max(eser, tür); eser indekste yoksa yalnızca tür.
    score: float
    monument: float | None
    genre: float


class ContextIndex:
    def __init__(
        self,
        keys: np.ndarray,
        chunks: np.ndarray,
        prototypes: np.ndarray,
        version: str,
        damage: np.ndarray | None = None,
        damage_labels: np.ndarray | None = None,
    ) -> None:
        unique, inverse = np.unique(keys, return_inverse=True)
        self._centroids = np.stack([centroid(chunks[inverse == i]) for i in range(len(unique))]).astype(np.float32)
        self._works = {str(key): row for row, key in enumerate(unique)}
        self._prototypes = prototypes.astype(np.float32)
        # Hasar prototipleri yoksa (eski indeks) hasar sinyali None döner.
        self._damage = damage.astype(np.float32) if damage is not None and len(damage) else None
        self._damage_labels = np.asarray(damage_labels) if self._damage is not None else None
        damage_count = 0 if self._damage is None else len(self._damage)
        self.info = ContextIndexInfo(version, len(unique), len(prototypes), damage_count)

    @classmethod
    def load(cls, path: Path) -> ContextIndex:
        with np.load(path, allow_pickle=False) as archive:
            info = json.loads(str(archive["info"]))
            damage = (archive["damage"], archive["damage_labels"]) if "damage" in archive.files else (None, None)
            return cls(archive["keys"], archive["chunks"], archive["prototypes"], info["version"], *damage)

    def damage(self, vectors: np.ndarray) -> list[DamageSignal] | None:
        if self._damage is None or self._damage_labels is None:
            return None
        scores = vectors @ self._damage.T
        k = min(TOP_K, scores.shape[1])
        relevance = np.sort(scores, axis=1)[:, -k:].mean(axis=1)
        categories = {}
        for category in sorted(set(self._damage_labels.tolist())):
            own = scores[:, self._damage_labels == category]
            categories[category] = np.sort(own, axis=1)[:, -min(CATEGORY_TOP_K, own.shape[1]) :].mean(axis=1)
        return [
            DamageSignal(round(float(relevance[i]), 4), {c: round(float(v[i]), 4) for c, v in categories.items()})
            for i in range(len(vectors))
        ]

    def relevance(self, vectors: np.ndarray, monument_id: str | None) -> list[Relevance]:
        k = min(TOP_K, len(self._prototypes))
        genre = np.sort(vectors @ self._prototypes.T, axis=1)[:, -k:].mean(axis=1)
        row = self._works.get(monument_id or "")
        monument = vectors @ self._centroids[row] if row is not None else None
        results = []
        for i, genre_score in enumerate(genre):
            own = None if monument is None else round(float(monument[i]), 4)
            results.append(Relevance(round(max(float(genre_score), own or -1.0), 4), own, round(float(genre_score), 4)))
        return results
