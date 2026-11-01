"""Bağlam (eserle ilgililik) eşiklerini ölçer, spam kurallarını denetler ve ``calibration-context.json`` yazar.

    python scripts/calibrate_context.py --models models/ --context models/context.npz --out calibration-context.json

Setler (repo, elle etiketli; prototiplerle ``data/context_prototypes.txt`` ortak cümle yok):
    data/relevance_eval.jsonl   eserle ilgili anılar (label 1: anı, kısa anı; eser anahtarlı) ve ilgisiz metinler
                                (label 0: konu dışı, spam, anlamsız)
    data/domain_eval.jsonl      zararlılık alan setinin temiz (label 0) metinleri: hepsi ziyaret anısı, eser
                                anahtarı yok (yalnızca anı türü benzerliği) → ek pozitif
    data/relevance_holdout.jsonl  eşik seçimine girmeyen ayrı set (yeni konular, yeni anılar): genelleme ölçüsü.
                                Prototip veya formül değişikliği burada da iyileşmiyorsa ezberdir.

Skor servisle aynıdır (``app.context``: max(eser merkezi, en yakın 5 prototipin ortalaması)). Eşik seçimi:
    approveFrom   spam olmayan ilgisizlerin (konu dışı + anlamsız) en çok ``--max-false-approve`` kadarının üstünde
                  kaldığı en düşük eşik: fotoğrafsız anıda bu değerin altı incelemeye düşer
    reviewBelow   eserle ilgili anıların en çok ``--max-miss`` kadarının altında kaldığı en yüksek eşik: bunun altı
                  fotoğraf esere benzese de incelemeye düşer (metin açıkça konu dışı)
Spam ilgisizler kurala bırakılır (eserin adını anan reklam yüksek bağlam alır); kural sonuçları ayrıca raporlanır.
Setler küçüktür (yüzlerce değil onlarca örnek): eşikler başlangıç değeridir, ``moderation-metrics`` ile izlenir.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import time
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
sys.path.insert(0, str(Path(__file__).resolve().parent))

from calibrate import auc

from app import spam
from app.context import ContextIndex, OnnxTextEmbedder

ROOT = Path(__file__).resolve().parents[1]
GRID = np.round(np.arange(0.10, 0.80, 0.01), 2)
QUANTILES = (0.01, 0.05, 0.1, 0.25, 0.5, 0.75, 0.9, 0.95, 0.99)


def load(path: Path) -> list[dict]:
    return [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line.strip()]


def describe(values: np.ndarray) -> dict:
    return {"n": len(values), **{f"p{int(q * 100):02d}": round(float(np.quantile(values, q)), 4) for q in QUANTILES}}


def share(mask: np.ndarray) -> float:
    return round(float(mask.mean()), 4) if len(mask) else 0.0


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--models", type=Path, required=True)
    parser.add_argument("--context", type=Path, required=True)
    parser.add_argument("--relevance", type=Path, default=ROOT / "data/relevance_eval.jsonl")
    parser.add_argument("--domain", type=Path, default=ROOT / "data/domain_eval.jsonl")
    parser.add_argument("--holdout", type=Path, default=ROOT / "data/relevance_holdout.jsonl")
    parser.add_argument("--max-false-approve", type=float, default=0.05)
    parser.add_argument("--max-miss", type=float, default=0.02)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()

    clean = [r["text"] for r in load(args.domain) if r["label"] == 0]
    rows = load(args.relevance) + [{"text": t, "label": 1, "monumentId": None, "kind": "domain"} for t in clean]
    holdout = load(args.holdout)
    embedder = OnnxTextEmbedder(args.models, threads=os.cpu_count() or 1)
    index = ContextIndex.load(args.context)

    def score(items: list[dict]) -> np.ndarray:
        return np.asarray([index.relevance(embedder.embed([r["text"]]), r["monumentId"])[0].score for r in items])

    started = time.perf_counter()
    scores = score(rows)
    ms = (time.perf_counter() - started) / len(rows) * 1000
    holdout_scores = score(holdout)
    holdout_labels = np.asarray([r["label"] for r in holdout])
    labels = np.asarray([r["label"] for r in rows])
    kinds = np.asarray([r["kind"] for r in rows])
    spam_scores = np.asarray([spam.check(r["text"]).score for r in rows])

    related = labels == 1
    unrelated = (labels == 0) & (kinds != "spam")
    approve_from = next((float(t) for t in GRID if (scores[unrelated] >= t).mean() <= args.max_false_approve), 0.8)
    review_below = max((float(t) for t in GRID if (scores[related] < t).mean() <= args.max_miss), default=0.1)
    if review_below > approve_from:
        review_below = approve_from

    by_kind = {
        kind: {
            "n": int((kinds == kind).sum()),
            "median": round(float(np.median(scores[kinds == kind])), 4),
            "approve": share(scores[kinds == kind] >= approve_from),
            "belowReview": share(scores[kinds == kind] < review_below),
            "spamReject": share(spam_scores[kinds == kind] >= spam.HIGH),
            "spamReview": share((spam_scores[kinds == kind] > 0) & (spam_scores[kinds == kind] < spam.HIGH)),
        }
        for kind in sorted(set(kinds))
    }
    result = {
        "context": {
            "name": embedder.info.name,
            "version": embedder.info.version,
            "embedding": embedder.info.embedding_model,
            "index": index.info.version,
            "relevance": "max(monument_centroid_cosine, top5_prototype_cosine_mean)",
        },
        "sets": {
            "related": describe(scores[related]),
            "unrelated": describe(scores[unrelated]),
            "spam": describe(scores[kinds == "spam"]),
            "auc": round(auc(scores[related | unrelated], labels[related | unrelated]), 4),
            "aucHoldout": round(auc(holdout_scores, holdout_labels), 4),
            "msPerText": round(ms, 1),
        },
        "thresholds": {
            "approveFrom": approve_from,
            "reviewBelow": review_below,
            "spamReviewFrom": 0.5,
            "spamRejectFrom": 0.9,
            "targets": {"maxFalseApprove": args.max_false_approve, "maxMissRelated": args.max_miss},
        },
        "bands": {
            "relatedApproved": share(scores[related] >= approve_from),
            "relatedBelowReview": share(scores[related] < review_below),
            "unrelatedApproved": share(scores[unrelated] >= approve_from),
            "unrelatedBelowReview": share(scores[unrelated] < review_below),
            "holdoutRelatedApproved": share(holdout_scores[holdout_labels == 1] >= approve_from),
            "holdoutRelatedBelowReview": share(holdout_scores[holdout_labels == 1] < review_below),
            "holdoutUnrelatedApproved": share(holdout_scores[holdout_labels == 0] >= approve_from),
            "spamCaughtByRules": share(spam_scores[kinds == "spam"] >= spam.HIGH),
            "nonSpamFlaggedByRules": share(spam_scores[kinds != "spam"] > 0),
            "byKind": by_kind,
        },
    }
    args.out.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({k: result[k] for k in ("sets", "thresholds")}, indent=2))
    print(json.dumps(result["bands"], indent=2))
    for i in np.argsort(holdout_scores):
        miss = holdout_labels[i] == 1 and holdout_scores[i] < approve_from
        if miss or (holdout_labels[i] == 0 and holdout_scores[i] >= approve_from):
            sign = "+" if holdout_labels[i] == 1 else "-"
            print(f"holdout {sign} {holdout_scores[i]:.3f} {holdout[i]['text'][:80]}")


if __name__ == "__main__":
    main()
