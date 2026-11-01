"""Görsel moderasyon eşiklerini ölçer ve ``calibration-vision.json`` yazar.

    python scripts/calibrate_vision.py --models models/ --references models/references.npz \\
        --seed ../../../seed --negatives <ilgisiz-fotoğraflar> --out calibration-vision.json

Benzerlik, servisteki gibi eser merkezine (``app.references``) kosinüstür. Setler:
    aynı eser       her referans görsel, kendi eserinin diğer referanslarının merkezine (bire-bir dışarıda
                    bırakma; özdeş kopyalar hariç). Ziyaretçi fotoğrafının en yakın vekili; envanter fotoğrafları
                    birbirine ziyaretçi fotoğrafından daha çok benzediği için iyimserdir.
    benzer eser     aynı görselin kendi eseri dışındaki en yakın eser merkezi (cami ↔ cami gibi zor negatif)
    ilgisiz         ``--negatives`` klasöründeki genel fotoğraflar (manzara, insan, nesne), her esere
    NSFW yanlış     NSFW modelinin tüm referans görsellerde ve ilgisiz fotoğraflarda verdiği skor
    pozitif

Seçim: ``similarityReviewBelow`` = aynı eser dağılımının %5'lik dilimi (envanter fotoğraflarının ≤ %5'i
incelemeye düşer). Benzerlik ile otomatik kaldırma kapalıdır (``null``): düşük benzerlik iç mekân, detay veya
insan ağırlıklı fotoğrafta da görülür. NSFW için gerçek pozitif set kullanılmaz; eşikler model kartının
(ikili sınıf, karar sınırı 0,5) üzerine alan setindeki yanlış pozitif oranıyla doğrulanır.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
sys.path.insert(0, str(Path(__file__).resolve().parent))

from build_references import references

from app.references import centroid
from app.vision import OnnxVisionModels, decode

MIN_REFERENCES = 3
DUPLICATE = 0.995
NSFW_REVIEW_FROM = 0.5
NSFW_REJECT_FROM = 0.9
QUANTILES = (0.01, 0.05, 0.1, 0.25, 0.5, 0.75, 0.9, 0.95, 0.99)
BUCKETS = ((3, 5), (6, 10), (11, 20), (21, 1000))


def describe(values: np.ndarray) -> dict:
    return {"n": len(values), **{f"p{int(q * 100):02d}": round(float(np.quantile(values, q)), 4) for q in QUANTILES}}


def below(values: np.ndarray, threshold: float) -> float:
    return round(float((values < threshold).mean()), 4)


def embed_all(models: OnnxVisionModels, images: list) -> np.ndarray:
    return np.concatenate([models.embed(images[i : i + 16]) for i in range(0, len(images), 16)])


def nsfw_all(models: OnnxVisionModels, images: list) -> np.ndarray:
    return np.asarray([s for i in range(0, len(images), 16) for s in models.nsfw(images[i : i + 16])])


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--models", type=Path, required=True)
    parser.add_argument("--references", type=Path, required=True)
    parser.add_argument("--seed", type=Path, required=True)
    parser.add_argument("--negatives", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()

    models = OnnxVisionModels(args.models, threads=os.cpu_count() or 1)
    with np.load(args.references, allow_pickle=False) as archive:
        keys, vectors = archive["keys"], archive["vectors"]
        index_info = json.loads(str(archive["info"]))
    groups = {str(key): np.flatnonzero(keys == key) for key in np.unique(keys)}
    eligible = {key: rows for key, rows in groups.items() if len(rows) >= MIN_REFERENCES}
    centroids = np.stack([centroid(vectors[rows]) for rows in eligible.values()])

    # Aynı eser (bire-bir dışarıda bırakma: görsel ve özdeş kopyaları merkezden çıkarılır) ve en yakın başka eser.
    same, lookalike, same_counts = [], [], []
    for position, rows in enumerate(eligible.values()):
        own = vectors[rows]
        for i in range(len(rows)):
            others = own[(own @ own[i]) < DUPLICATE]
            if len(others) < MIN_REFERENCES:
                continue
            same.append(float(centroid(others) @ own[i]))
            same_counts.append(len(rows))
            scores = centroids @ own[i]
            scores[position] = -1
            lookalike.append(float(scores.max()))
    same, lookalike, same_counts = np.asarray(same), np.asarray(lookalike), np.asarray(same_counts)

    # İlgisiz fotoğraflar: her eserin merkezine (dağılım referans sayısından bağımsız olmalı).
    negative_paths = sorted(p for p in args.negatives.iterdir() if p.suffix.lower() in {".jpg", ".jpeg", ".png"})
    negative_images = [decode(p.read_bytes()) for p in negative_paths]
    unrelated_matrix = embed_all(models, negative_images) @ centroids.T
    unrelated = unrelated_matrix.ravel()
    work_counts = np.asarray([len(rows) for rows in eligible.values()])

    review_below = round(float(np.floor(np.quantile(same, 0.05) * 100) / 100), 2)

    by_references = {}
    for low, high in BUCKETS:
        own = same[(same_counts >= low) & (same_counts <= high)]
        other = unrelated_matrix[:, (work_counts >= low) & (work_counts <= high)].ravel()
        by_references[f"{low}-{high}"] = {
            "works": int(((work_counts >= low) & (work_counts <= high)).sum()),
            "sameMonumentBelowReview": below(own, review_below),
            "unrelatedBelowReview": below(other, review_below),
            "unrelatedP95": round(float(np.quantile(other, 0.95)), 4),
        }

    # NSFW: yalnızca güvenli içerik → yanlış pozitif oranı.
    paths = sorted({path for paths in references(args.seed).values() for path in paths})
    catalog_nsfw = nsfw_all(models, [decode(p.read_bytes()) for p in paths])
    negative_nsfw = nsfw_all(models, negative_images)
    safe = np.concatenate([catalog_nsfw, negative_nsfw])

    result = {
        "vision": {
            "name": models.info.name,
            "version": models.info.version,
            "nsfw": models.info.nsfw_model,
            "embedding": models.info.embedding_model,
            "similarity": "centroid_cosine",
        },
        "references": index_info,
        "sets": {
            "sameMonument": describe(same),
            "lookalikeMonument": describe(lookalike),
            "unrelated": describe(unrelated),
            "nsfwCatalog": describe(catalog_nsfw),
            "nsfwUnrelated": describe(negative_nsfw),
        },
        "thresholds": {
            "nsfwReviewFrom": NSFW_REVIEW_FROM,
            "nsfwRejectFrom": NSFW_REJECT_FROM,
            "similarityReviewBelow": review_below,
            "similarityRejectBelow": None,
            "similarityMinReferences": MIN_REFERENCES,
        },
        "bands": {
            "sameMonumentBelowReview": below(same, review_below),
            "lookalikeBelowReview": below(lookalike, review_below),
            "unrelatedBelowReview": below(unrelated, review_below),
            "byReferenceCount": by_references,
            # Otomatik kaldırma açılırsa (similarityRejectBelow) aday değerlerin bedeli ve kazancı.
            "rejectCandidates": {
                str(t): {"sameMonument": below(same, t), "unrelated": below(unrelated, t)}
                for t in (0.02, 0.04, 0.06, 0.08)
            },
            "safeImagesAtOrAboveNsfwReview": round(float((safe >= NSFW_REVIEW_FROM).mean()), 4),
            "safeImagesAtOrAboveNsfwReject": round(float((safe >= NSFW_REJECT_FROM).mean()), 4),
            "safeImages": len(safe),
        },
    }
    args.out.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({k: result[k] for k in ("thresholds", "bands")}, indent=2))


if __name__ == "__main__":
    main()
