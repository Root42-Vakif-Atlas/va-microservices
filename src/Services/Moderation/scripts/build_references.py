"""Seed verisinden eser referans indeksini üretir (DINOv2 gömmeleri, eser anahtarına göre).

    python scripts/build_references.py --seed ../../../seed --models models/ --out models/references.npz

``--seed`` altındaki her ``root42-veri.json`` okunur (Catalog'a içe aktarılan dosyanın aynısı); bir eserin
referansları: eser görselleri + bölüm (part) görselleri + üyesi olduğu külliyenin görselleri. Anahtarlar
Catalog'daki ``works.key`` değerleridir, Memories ``monumentId`` olarak bunları gönderir.

Yeni şehir içe aktarıldığında veya görseller değiştiğinde indeks yeniden üretilir (imaj yeniden derlenir ya da
``REFERENCES_PATH`` ile yeni dosya bağlanır).
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

from app.vision import OnnxVisionModels, UnreadableImage, decode

DATA_FILE = "root42-veri.json"
BATCH = 16


def _resolve(root: Path, images: list[dict] | None) -> list[Path]:
    """Görsel yolları dosyaya görelidir (şema); klasör dışına çıkan veya eksik dosya atlanır."""
    paths = [(root / image["src"]).resolve() for image in images or []]
    return [p for p in paths if p.is_relative_to(root) and p.is_file()]


def references(seed: Path) -> dict[str, list[Path]]:
    """Eser anahtarı → görsel dosyaları (yinelenenler bir kez)."""
    result: dict[str, list[Path]] = defaultdict(list)
    for data_file in sorted(seed.rglob(DATA_FILE)):
        root = data_file.parent.resolve()
        data = json.loads(data_file.read_text(encoding="utf-8"))
        for work in data.get("works", []):
            result[work["key"]] += _resolve(root, work.get("images"))
            for part in work.get("parts") or []:
                result[work["key"]] += _resolve(root, part.get("images"))
        for complex_ in data.get("complexes", []):
            images = _resolve(root, complex_.get("images"))
            for key in complex_.get("works") or []:
                result[key] += images
    return {key: list(dict.fromkeys(paths)) for key, paths in result.items() if paths}


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--seed", type=Path, required=True)
    parser.add_argument("--models", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()

    works = references(args.seed)
    if not works:
        raise SystemExit(f"no {DATA_FILE} with images under {args.seed}")
    rows = [(key, path) for key, paths in sorted(works.items()) for path in paths]
    models = OnnxVisionModels(args.models, threads=os.cpu_count() or 1)

    keys, vectors, skipped = [], [], 0
    for start in range(0, len(rows), BATCH):
        batch, images = [], []
        for key, path in rows[start : start + BATCH]:
            try:
                images.append(decode(path.read_bytes()))
                batch.append(key)
            except UnreadableImage:
                skipped += 1
        if images:
            vectors.append(models.embed(images))
            keys += batch
        if (start // BATCH) % 32 == 0 or start + BATCH >= len(rows):
            print(f"{min(start + BATCH, len(rows))}/{len(rows)} images", file=sys.stderr)

    matrix = np.concatenate(vectors).astype(np.float32)
    key_array = np.asarray(keys)
    digest = hashlib.sha256(matrix.tobytes() + "\n".join(keys).encode()).hexdigest()
    info = {
        "version": digest[:12],
        "createdAt": datetime.now(UTC).isoformat(timespec="seconds"),
        "vision": f"{models.info.name}/{models.info.version}",
        "embedding": models.info.embedding_model,
        "works": len(set(keys)),
        "images": len(keys),
        "skipped": skipped,
        "sources": sorted(str(p.relative_to(args.seed)) for p in args.seed.rglob(DATA_FILE)),
    }
    args.out.parent.mkdir(parents=True, exist_ok=True)
    temp = args.out.with_suffix(".tmp.npz")
    np.savez_compressed(temp, keys=key_array, vectors=matrix, info=np.asarray(json.dumps(info)))
    temp.replace(args.out)
    print(json.dumps(info, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
