"""Eser referans indeksi: katalog görsellerinin DINOv2 gömmeleri, eser anahtarına (works.key) göre gruplu.

``references.npz`` ``scripts/build_references.py`` ile seed verisinden üretilir (VGM içe aktarımıyla aynı
``root42-veri.json`` dosyaları; anahtarlar Catalog'daki eser anahtarlarıdır).

Benzerlik = fotoğrafın, eserin referanslarının ortalama yönüne (normalize merkez) kosinüsü; olasılık değildir.
Referanslar arasındaki en yüksek benzerlik kullanılmaz: referans sayısı arttıkça ilgisiz fotoğrafın skoru da
yükselir (3–5 referansta medyan 0,05 → 51+ referansta 0,12); merkezde ilgisiz fotoğrafın dağılımı referans
sayısından bağımsızdır (medyan ~0,01), tek bir eşik her esere uyar (calibration-vision.json).

En yakın başka eser döndürülmez: envanter fotoğrafının en yakın başka eseri çoğu zaman kendi eserinden daha
benzerdir (cami ↔ cami); "hangi eser" sorusu bu modelle güvenilir yanıtlanamaz, yalnızca "bu eserle ilgili mi".

Katalog kopyası (``copy``): fotoğrafın indeksteki tek tek görsellere (tüm eserler) en yüksek kosinüsü. Katalog
görselinin küçültülmüş/yeniden sıkıştırılmış kopyası ≥ 0,95 alır; aynı eserin başka katalog fotoğrafı p99 0,92
(``calibration-evidence.json``). Hasar bildiriminde "fotoğraf internetten/katalogdan alınmış olabilir" işaretidir.
"""

from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path

import numpy as np


def centroid(vectors: np.ndarray) -> np.ndarray:
    """Normalize gömmelerin normalize ortalaması."""
    mean = vectors.mean(axis=0)
    return mean / max(float(np.linalg.norm(mean)), 1e-12)


@dataclass(frozen=True)
class ReferenceInfo:
    version: str
    works: int
    images: int


@dataclass(frozen=True)
class Match:
    # None: eserin indekste referansı yok (karar katmanı benzerlik kuralını uygulamaz).
    similarity: float | None
    references: int
    # Tüm indeksteki tek bir görsele en yüksek kosinüs (katalog kopyası); indeks boşsa None.
    copy: float | None = None


class ReferenceIndex:
    def __init__(self, keys: np.ndarray, vectors: np.ndarray, version: str) -> None:
        unique, inverse, counts = np.unique(keys, return_inverse=True, return_counts=True)
        centroids = np.stack([centroid(vectors[inverse == i]) for i in range(len(unique))]).astype(np.float32)
        self._works = {str(key): (row, int(count)) for row, (key, count) in enumerate(zip(unique, counts, strict=True))}
        self._centroids = centroids
        self._vectors = vectors.astype(np.float32)
        self.info = ReferenceInfo(version, len(unique), len(keys))

    @classmethod
    def load(cls, path: Path) -> ReferenceIndex:
        with np.load(path, allow_pickle=False) as archive:
            info = json.loads(str(archive["info"]))
            return cls(archive["keys"], archive["vectors"], info["version"])

    def match(self, vector: np.ndarray, monument_id: str | None) -> Match:
        copy = round(float((self._vectors @ vector).max()), 4) if len(self._vectors) else None
        row, count = self._works.get(monument_id or "", (-1, 0))
        if not count:
            return Match(None, 0, copy)
        return Match(round(float(self._centroids[row] @ vector), 4), count, copy)
