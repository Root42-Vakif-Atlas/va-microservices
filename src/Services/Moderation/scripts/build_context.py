"""Seed verisinden bağlam indeksini üretir (eser metin parçaları + anı türü ve hasar türü prototipleri).

    python scripts/build_context.py --seed ../../../seed --models models/ --out models/context.npz

``--seed`` altındaki her ``root42-veri.json`` okunur (``build_references.py`` ile aynı dosyalar, aynı anahtarlar).
Bir eserin parçaları: Türkçe ad (+ takma ad, + üyesi olduğu külliyenin adı) ve bölüm açıklamalarının 400 karakterlik
dilimleri (en çok 12, her dilimin başında ad); servis bunların normalize merkezini kullanır. Açıklaması olmayan
eserde yalnızca ad. Prototipler ``data/context_prototypes.txt`` (``#`` ile başlayan satırlar yorum) ve hasar türü
prototipleri ``data/damage_prototypes.json``'dur (tür → cümleler; Sivil Koruma kanıt değerlendirmesi).

Yeni şehir içe aktarıldığında veya prototipler değiştiğinde indeks yeniden üretilir ve
``scripts/calibrate_context.py`` yeniden çalıştırılır (imaj yeniden derlenir ya da ``CONTEXT_PATH`` ile yeni dosya
bağlanır).
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import sys
from collections import defaultdict
from datetime import UTC, datetime
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from app.context import OnnxTextEmbedder

DATA_FILE = "root42-veri.json"
PROTOTYPES = Path(__file__).resolve().parents[1] / "data/context_prototypes.txt"
DAMAGE_PROTOTYPES = Path(__file__).resolve().parents[1] / "data/damage_prototypes.json"
CHUNK = 400
MAX_CHUNKS = 12
BATCH = 32


def _tr(translations: dict | None) -> dict:
    return (translations or {}).get("tr") or {}


def passages(seed: Path) -> dict[str, list[str]]:
    """Eser anahtarı → gömülecek metin parçaları."""
    complexes: dict[str, str] = {}
    works: list[dict] = []
    for data_file in sorted(seed.rglob(DATA_FILE)):
        data = json.loads(data_file.read_text(encoding="utf-8"))
        works += data.get("works", [])
        for complex_ in data.get("complexes", []):
            name = (complex_.get("name") or {}).get("tr")
            for key in complex_.get("works") or []:
                if name:
                    complexes[key] = name

    result: dict[str, list[str]] = defaultdict(list)
    for work in works:
        tr = _tr(work.get("translations"))
        header = " — ".join(part for part in (tr.get("title"), tr.get("nickname"), complexes.get(work["key"])) if part)
        description = " ".join(
            text for part in work.get("parts") or [] if (text := _tr(part.get("translations")).get("description"))
        ).strip()
        chunks = [f"{header}. {description[i : i + CHUNK]}" for i in range(0, len(description), CHUNK)][:MAX_CHUNKS]
        result[work["key"]] = chunks or [header]
    return dict(result)


def prototypes(path: Path = PROTOTYPES) -> list[str]:
    lines = (line.strip() for line in path.read_text(encoding="utf-8").splitlines())
    return [line for line in lines if line and not line.startswith("#")]


def damage_prototypes(path: Path = DAMAGE_PROTOTYPES) -> list[tuple[str, str]]:
    """(tür, cümle) çiftleri; ``_`` ile başlayan anahtarlar yorum."""
    spec = json.loads(path.read_text(encoding="utf-8"))
    return [(category, text) for category, texts in spec.items() if not category.startswith("_") for text in texts]


def embed_all(embedder: OnnxTextEmbedder, texts: list[str]) -> np.ndarray:
    return np.concatenate([embedder.embed(texts[i : i + BATCH]) for i in range(0, len(texts), BATCH)])


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--seed", type=Path, required=True)
    parser.add_argument("--models", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()

    works = passages(args.seed)
    if not works:
        raise SystemExit(f"no {DATA_FILE} under {args.seed}")
    rows = [(key, text) for key, texts in sorted(works.items()) for text in texts]
    examples = prototypes()
    embedder = OnnxTextEmbedder(args.models, threads=os.cpu_count() or 1)

    chunks = embed_all(embedder, [text for _, text in rows]).astype(np.float32)
    prototype_vectors = embed_all(embedder, examples).astype(np.float32)
    damage = damage_prototypes()
    damage_vectors = embed_all(embedder, [text for _, text in damage]).astype(np.float32)
    damage_labels = [category for category, _ in damage]
    keys = [key for key, _ in rows]
    digest = hashlib.sha256(
        chunks.tobytes()
        + prototype_vectors.tobytes()
        + damage_vectors.tobytes()
        + "\n".join(keys + damage_labels).encode()
    ).hexdigest()
    info = {
        "version": digest[:12],
        "createdAt": datetime.now(UTC).isoformat(timespec="seconds"),
        "context": f"{embedder.info.name}/{embedder.info.version}",
        "embedding": embedder.info.embedding_model,
        "works": len(works),
        "chunks": len(rows),
        "prototypes": len(examples),
        "damagePrototypes": len(damage),
        "sources": sorted(str(p.relative_to(args.seed)) for p in args.seed.rglob(DATA_FILE)),
    }
    args.out.parent.mkdir(parents=True, exist_ok=True)
    temp = args.out.with_suffix(".tmp.npz")
    np.savez_compressed(
        temp,
        keys=np.asarray(keys),
        chunks=chunks,
        prototypes=prototype_vectors,
        damage=damage_vectors,
        damage_labels=np.asarray(damage_labels),
        info=np.asarray(json.dumps(info)),
    )
    temp.replace(args.out)
    print(json.dumps(info, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
