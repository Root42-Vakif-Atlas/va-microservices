"""Eşik kalibrasyonu ve INT8 ↔ FP32 doğruluk karşılaştırması (ADR 58).

    python scripts/calibrate.py --model models/ --data /path/to/eval --out calibration.json

``--data`` altında beklenenler (README → Kalibrasyon):
    offenseval-tr-testset-v1.tsv + offenseval-tr-labela-v1.tsv   OffensEval 2020 TR test (CC BY 2.0)
    civil_test.parquet                                           google/civil_comments test (CC0)
    domain_eval.jsonl (repo: data/)                              anı benzeri elle etiketli örnekler

Skor, servisle aynı birleşimdir (model + Türkçe sözlük; ``ModerationEngine``) ve kategorilerin en
yükseğidir. Eşik seçimi:
    reject_from   kesinliğin (precision) Civil Comments ve alan setinde ≥ ``--precision`` olduğu en düşük
                  eşik. OffensEval kesinlik kısıtına girmez: etiket tanımı farklı (küfürlü ama hedefsiz
                  tweetler "NOT"), yalnızca raporlanır.
    approve_below otomatik onaylananlar içindeki zararlı payının Civil Comments'te ≤ ``--max-false-approve``
                  ve alan setinde zararlıların en çok ``--max-miss`` kadarının otomatik onaya kaçtığı en
                  yüksek eşik.
"""

from __future__ import annotations

import argparse
import csv
import json
import random
import sys
import time
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from app.engine import ModerationEngine
from app.model import OnnxToxicityModel

GRID = np.round(np.arange(0.05, 0.99, 0.01), 2)


def load_offenseval(data: Path) -> tuple[list[str], np.ndarray]:
    texts: dict[str, str] = {}
    with (data / "offenseval-tr-testset-v1.tsv").open(encoding="utf-8") as file:
        for row in csv.DictReader(file, delimiter="\t", quoting=csv.QUOTE_NONE):
            texts[row["id"]] = row["tweet"]
    ids, labels = [], []
    with (data / "offenseval-tr-labela-v1.tsv").open(encoding="utf-8") as file:
        for line in file:
            key, label = line.strip().split(",")
            ids.append(key)
            labels.append(1 if label == "OFF" else 0)
    return [texts[i] for i in ids], np.asarray(labels)


def load_civil(data: Path, size: int, seed: int) -> tuple[list[str], np.ndarray]:
    import pyarrow.parquet as pq

    table = pq.read_table(data / "civil_test.parquet", columns=["text", "toxicity"]).to_pylist()
    rng = random.Random(seed)
    toxic = [r for r in table if r["toxicity"] >= 0.5]
    clean = [r for r in table if r["toxicity"] < 0.5]
    # Gerçek oran (~%8) korunur; örneklem yalnızca hesap süresini kısaltır.
    share = len(toxic) / len(table)
    rows = rng.sample(toxic, round(size * share)) + rng.sample(clean, size - round(size * share))
    return [r["text"][:1000] for r in rows], np.asarray([1 if r["toxicity"] >= 0.5 else 0 for r in rows])


def load_domain(path: Path) -> tuple[list[str], np.ndarray]:
    rows = [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line.strip()]
    return [r["text"] for r in rows], np.asarray([r["label"] for r in rows])


def auc(scores: np.ndarray, labels: np.ndarray) -> float:
    """Mann–Whitney U ile ROC AUC (bağlar ortalama sıra)."""
    order = scores.argsort()
    ranks = np.empty(len(scores))
    ranks[order] = np.arange(1, len(scores) + 1)
    for value in np.unique(scores):
        tied = scores == value
        ranks[tied] = ranks[tied].mean()
    positives = labels == 1
    n_pos, n_neg = positives.sum(), (~positives).sum()
    return float((ranks[positives].sum() - n_pos * (n_pos + 1) / 2) / (n_pos * n_neg))


def score_all(engine: ModerationEngine, texts: list[str], batch: int = 16) -> tuple[np.ndarray, float]:
    result: list[float] = []
    started = time.perf_counter()
    for i in range(0, len(texts), batch):
        result.extend(max(c.scores.values()) for c in engine.classify(texts[i : i + batch]))
    return np.asarray(result), (time.perf_counter() - started) / len(texts) * 1000


def curve(scores: np.ndarray, labels: np.ndarray) -> list[dict[str, float]]:
    rows = []
    toxic = labels == 1
    for t in GRID:
        flagged = scores >= t
        precision = float((flagged & toxic).sum() / flagged.sum()) if flagged.any() else 1.0
        rows.append(
            {
                "threshold": float(t),
                "precision": round(precision, 4),
                "recall": round(float((flagged & toxic).sum() / toxic.sum()), 4),
                "missBelow": round(float(((scores < t) & toxic).sum() / toxic.sum()), 4),
                "falseApproveRate": round(float(((scores < t) & toxic).sum() / max((scores < t).sum(), 1)), 4),
            }
        )
    return rows


def bands(scores: np.ndarray, approve_below: float, reject_from: float) -> dict[str, float]:
    n = len(scores)
    return {
        "autoApprove": round(float((scores < approve_below).sum() / n), 4),
        "review": round(float(((scores >= approve_below) & (scores < reject_from)).sum() / n), 4),
        "autoReject": round(float((scores >= reject_from).sum() / n), 4),
    }


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", type=Path, required=True, help="INT8 model dizini")
    parser.add_argument("--fp32", type=Path, help="FP32 model dizini (karşılaştırma)")
    parser.add_argument("--data", type=Path, required=True)
    parser.add_argument("--domain", type=Path, default=Path(__file__).resolve().parents[1] / "data/domain_eval.jsonl")
    parser.add_argument("--civil-size", type=int, default=3000)
    parser.add_argument("--precision", type=float, default=0.97)
    parser.add_argument("--max-miss", type=float, default=0.10)
    parser.add_argument("--max-false-approve", type=float, default=0.01)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()

    sets = {
        "offenseval_tr": load_offenseval(args.data),
        "civil_comments_en": load_civil(args.data, args.civil_size, seed=58),
        "domain": load_domain(args.domain),
    }
    engine = ModerationEngine(OnnxToxicityModel(args.model, threads=4), languages=None)
    scored = {name: (*score_all(engine, texts), labels) for name, (texts, labels) in sets.items()}

    report: dict[str, object] = {"model": {"name": engine.info.name, "version": engine.info.version}, "sets": {}}
    for name, (scores, ms, labels) in scored.items():
        entry: dict[str, object] = {
            "size": len(labels),
            "toxicShare": round(float(labels.mean()), 4),
            "auc": round(auc(scores, labels), 4),
            "msPerText": round(ms, 1),
            "curve": curve(scores, labels),
        }
        if args.fp32:
            fp32 = ModerationEngine(OnnxToxicityModel(args.fp32, threads=4), languages=None)
            fp32_scores, _ = score_all(fp32, sets[name][0])
            entry["aucFp32"] = round(auc(fp32_scores, labels), 4)
            entry["maxAbsDiff"] = round(float(np.abs(fp32_scores - scores).max()), 4)
        report["sets"][name] = entry

    def precision_ok(t: float, name: str) -> bool:
        scores, _, labels = scored[name]
        flagged = scores >= t
        return not flagged.any() or (flagged & (labels == 1)).sum() / flagged.sum() >= args.precision

    reject_from = next(
        (float(t) for t in GRID if all(precision_ok(t, n) for n in ("civil_comments_en", "domain"))),
        0.95,
    )

    def approve_ok(t: float) -> bool:
        civil_scores, _, civil_labels = scored["civil_comments_en"]
        approved = civil_scores < t
        false_approve = (approved & (civil_labels == 1)).sum() / max(approved.sum(), 1)
        domain_scores, _, domain_labels = scored["domain"]
        missed = (domain_scores[domain_labels == 1] < t).mean()
        return false_approve <= args.max_false_approve and missed <= args.max_miss

    approve_below = max((float(t) for t in GRID if t < reject_from and approve_ok(t)), default=0.05)

    report["thresholds"] = {
        "approveBelow": approve_below,
        "rejectFrom": reject_from,
        "targets": {
            "precision": args.precision,
            "maxMissDomain": args.max_miss,
            "maxFalseApproveCivil": args.max_false_approve,
        },
    }
    report["bands"] = {name: bands(scores, approve_below, reject_from) for name, (scores, _, _) in scored.items()}
    args.out.write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")

    print(json.dumps(report["thresholds"], indent=2))
    for name, entry in report["sets"].items():
        extra = f" fp32={entry['aucFp32']} Δmax={entry['maxAbsDiff']}" if "aucFp32" in entry else ""
        print(
            f"{name}: n={entry['size']} auc={entry['auc']}{extra} {entry['msPerText']} ms/text {report['bands'][name]}"
        )


if __name__ == "__main__":
    main()
