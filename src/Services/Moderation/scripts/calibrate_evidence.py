"""Sivil Koruma kanıt sinyallerinin eşiklerini ölçer ve ``calibration-evidence.json`` yazar.

    python scripts/calibrate_evidence.py --models models/ --context models/context.npz \\
        --references models/references.npz --seed ../../../seed --out calibration-evidence.json

Hasar açıklaması (``app.context.ContextIndex.damage``):
    data/damage_eval.jsonl      65 hasar bildirimi (türlü) + 37 hasar olmayan metin (anı, şikâyet, konu dışı, test,
                                spam) → eşik seçimi
    data/damage_holdout.jsonl   eşik seçimine girmeyen ayrı set → yalnızca raporlama
    supportsFrom    hasar olmayanların en çok ``--max-false-support`` kadarının üstünde kaldığı en düşük eşik:
                    üstü "açıklama hasar tarif ediyor" (destekler)
    underminesBelow hasar bildirimlerinin hiçbirinin altında kalmadığı en yüksek eşikten ``--safety`` kadar aşağısı:
                    altı "açıklama hasar tarif etmiyor" (şüphe; politika bunu tek başına kırmızı yapmaz)
    Tür uyumu yalnızca raporlanır (doğru tür ilk sırada / pay içinde); etiket kararına girmez.

Katalog kopyası (``app.references``: tüm indeksteki tek görsele en yüksek kosinüs):
    pozitif   seed görsellerinin kopyaları (640 px JPEG, %80 kırpma, düşük kalite 400 px, parlaklık + 800 px)
    negatif   aynı görselin indeksteki diğer (özdeş olmayan) görsellere en yüksek benzerliği: aynı eserin başka
              katalog fotoğrafı — gerçek ziyaretçi/hasar fotoğrafından daha zor negatif
    copyFrom  negatiflerin ≤ ``--max-copy-false`` kadarının üstünde kaldığı en düşük eşik
"""

from __future__ import annotations

import argparse
import io
import json
import os
import random
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
sys.path.insert(0, str(Path(__file__).resolve().parent))

from build_references import references
from calibrate import auc

from app.context import ContextIndex, OnnxTextEmbedder
from app.vision import OnnxVisionModels, decode

ROOT = Path(__file__).resolve().parents[1]
GRID = np.round(np.arange(0.10, 0.99, 0.01), 2)
DUPLICATE = 0.995
QUANTILES = (0.0, 0.01, 0.05, 0.25, 0.5, 0.75, 0.95, 0.99, 1.0)


def load(path: Path) -> list[dict]:
    return [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line.strip()]


def describe(values: np.ndarray) -> dict:
    return {"n": len(values), **{f"p{int(q * 100):02d}": round(float(np.quantile(values, q)), 4) for q in QUANTILES}}


def share(mask: np.ndarray) -> float:
    return round(float(mask.mean()), 4) if len(mask) else 0.0


def damage_signals(embedder: OnnxTextEmbedder, index: ContextIndex, rows: list[dict]):
    signals = index.damage(embedder.embed([r["text"] for r in rows]))
    if signals is None:
        raise SystemExit("context index has no damage prototypes (rebuild with scripts/build_context.py)")
    return np.asarray([s.relevance for s in signals]), [s.categories for s in signals]


def categories_report(rows: list[dict], categories: list[dict[str, float]], margin: float) -> dict:
    claimed = [(r, c) for r, c in zip(rows, categories, strict=True) if r["label"] == 1 and r["category"] != "other"]
    top1 = np.mean([max(c, key=c.get) == r["category"] for r, c in claimed])
    within = np.mean([c[r["category"]] >= max(c.values()) - margin for r, c in claimed])
    wrong = np.mean([c[o] >= max(c.values()) - margin for r, c in claimed for o in c if o != r["category"]])
    return {
        "n": len(claimed),
        "top1": round(float(top1), 4),
        "trueWithinMargin": round(float(within), 4),
        "wrongWithinMargin": round(float(wrong), 4),
        "margin": margin,
    }


def copies(image) -> list:
    def jpeg(img, quality: int, size: int | None):
        if size:
            img = img.copy()
            img.thumbnail((size, size))
        buffer = io.BytesIO()
        img.save(buffer, "JPEG", quality=quality)
        return decode(buffer.getvalue())

    from PIL import ImageEnhance

    width, height = image.size
    cropped = image.crop((int(width * 0.1), int(height * 0.1), int(width * 0.9), int(height * 0.9)))
    return [
        jpeg(image, 75, 640),
        jpeg(cropped, 75, 640),
        jpeg(image, 40, 400),
        jpeg(ImageEnhance.Brightness(image).enhance(1.15), 70, 800),
    ]


COPY_KINDS = ("thumbnail640", "crop80", "lowQuality400", "brightness800")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--models", type=Path, required=True)
    parser.add_argument("--context", type=Path, required=True)
    parser.add_argument("--references", type=Path, required=True)
    parser.add_argument("--seed", type=Path, required=True)
    parser.add_argument("--eval", type=Path, default=ROOT / "data/damage_eval.jsonl")
    parser.add_argument("--holdout", type=Path, default=ROOT / "data/damage_holdout.jsonl")
    parser.add_argument("--max-false-support", type=float, default=0.05)
    parser.add_argument("--safety", type=float, default=0.05)
    parser.add_argument("--category-margin", type=float, default=0.05)
    parser.add_argument("--copy-sample", type=int, default=300)
    parser.add_argument("--max-copy-false", type=float, default=0.01)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()

    threads = os.cpu_count() or 1
    embedder = OnnxTextEmbedder(args.models, threads=threads)
    index = ContextIndex.load(args.context)
    rows, holdout = load(args.eval), load(args.holdout)
    scores, categories = damage_signals(embedder, index, rows)
    holdout_scores, holdout_categories = damage_signals(embedder, index, holdout)
    labels = np.asarray([r["label"] for r in rows])
    holdout_labels = np.asarray([r["label"] for r in holdout])

    supports_from = next(float(t) for t in GRID if (scores[labels == 0] >= t).mean() <= args.max_false_support)
    undermines_below = round(max(0.0, float(np.floor(scores[labels == 1].min() * 100) / 100) - args.safety), 2)

    # Katalog kopyası.
    vision = OnnxVisionModels(args.models, threads=threads)
    with np.load(args.references, allow_pickle=False) as archive:
        vectors, index_info = archive["vectors"], json.loads(str(archive["info"]))
    rows_images = [path for _, paths in sorted(references(args.seed).items()) for path in paths]
    if len(rows_images) != len(vectors):
        raise SystemExit("references.npz does not match --seed (rebuild with scripts/build_references.py)")
    sample = random.Random(58).sample(range(len(vectors)), min(args.copy_sample, len(vectors)))
    positives: dict[str, list[float]] = {kind: [] for kind in COPY_KINDS}
    negatives = []
    for i in sample:
        embedded = vision.embed(copies(decode(rows_images[i].read_bytes())))
        for kind, vector in zip(COPY_KINDS, embedded, strict=True):
            positives[kind].append(float((vectors @ vector).max()))
        others = vectors @ vectors[i]
        others[others >= DUPLICATE] = -1
        negatives.append(float(others.max()))
    negatives_array = np.asarray(negatives)
    copy_from = next(float(t) for t in GRID if (negatives_array >= t).mean() <= args.max_copy_false)

    result = {
        "context": {
            "embedding": embedder.info.embedding_model,
            "index": index.info.version,
            "damagePrototypes": index.info.damage_prototypes,
            "damage": "top5_mean_cosine(damage_prototypes); category: top3_mean_cosine(category_prototypes)",
        },
        "references": {"version": index_info["version"], "images": len(vectors)},
        "damage": {
            "sets": {
                "damage": describe(scores[labels == 1]),
                "notDamage": describe(scores[labels == 0]),
                "auc": round(auc(scores, labels), 4),
                "aucHoldout": round(auc(holdout_scores, holdout_labels), 4),
            },
            "bands": {
                "damageSupported": share(scores[labels == 1] >= supports_from),
                "notDamageSupported": share(scores[labels == 0] >= supports_from),
                "damageUndermined": share(scores[labels == 1] < undermines_below),
                "notDamageUndermined": share(scores[labels == 0] < undermines_below),
                "holdoutDamageSupported": share(holdout_scores[holdout_labels == 1] >= supports_from),
                "holdoutNotDamageSupported": share(holdout_scores[holdout_labels == 0] >= supports_from),
                "holdoutDamageUndermined": share(holdout_scores[holdout_labels == 1] < undermines_below),
                "holdoutNotDamageUndermined": share(holdout_scores[holdout_labels == 0] < undermines_below),
            },
            "categories": {
                "eval": categories_report(rows, categories, args.category_margin),
                "holdout": categories_report(holdout, holdout_categories, args.category_margin),
                "note": "yalnızca bilgi: etiket kararına girmez",
            },
        },
        "catalogCopy": {
            "sample": len(sample),
            "copies": {kind: describe(np.asarray(values)) for kind, values in positives.items()},
            "otherCatalogPhotos": describe(negatives_array),
            "recall": {kind: share(np.asarray(values) >= copy_from) for kind, values in positives.items()},
            "falsePositive": share(negatives_array >= copy_from),
        },
        "thresholds": {
            "damageSupportsFrom": supports_from,
            "damageUnderminesBelow": undermines_below,
            "catalogCopyFrom": copy_from,
            "categoryMargin": args.category_margin,
            "targets": {
                "maxFalseSupport": args.max_false_support,
                "safety": args.safety,
                "maxCopyFalse": args.max_copy_false,
            },
        },
    }
    args.out.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(result["thresholds"], indent=2))
    print(json.dumps(result["damage"]["bands"], indent=2))
    print(json.dumps(result["damage"]["categories"], indent=2))
    print(json.dumps({k: result["catalogCopy"][k] for k in ("recall", "falsePositive")}, indent=2))
    for i in np.argsort(holdout_scores):
        label = holdout_labels[i]
        if (label == 1 and holdout_scores[i] < supports_from) or (label == 0 and holdout_scores[i] >= undermines_below):
            print(f"holdout {'+' if label else '-'} {holdout_scores[i]:.3f} {holdout[i]['text'][:80]}")


if __name__ == "__main__":
    main()
