"""Dil desteği kontrolü: model yalnızca eğitildiği dillerde güvenilirdir (Detoxify çok dilli).

Desteklenmeyen dilde düşük skor "temiz" anlamına gelmez; karar katmanı (Memories) böyle metni
otomatik onaylamaz, incelemeye bırakır. Kısa metinde dil tespiti güvenilmez olduğundan yalnızca
yüksek güvenli tespit veya Latin/Kiril dışı alfabe "desteklenmiyor" sayılır.
"""

from __future__ import annotations

import unicodedata
from dataclasses import dataclass

SUPPORTED = frozenset({"tr", "en", "es", "fr", "it", "pt", "ru"})
# Türkçeye yakın Türk dilleri kısa Türkçe metinde sık karışır ("crh", "az").
ALIASES = {"az": "tr", "crh": "tr", "tk": "tr"}
CONFIDENCE = 0.9
MIN_LETTERS = 12


@dataclass(frozen=True)
class LanguageResult:
    code: str | None
    supported: bool


class LanguageDetector:
    def __init__(self) -> None:
        from py3langid.langid import MODEL_FILE, LanguageIdentifier

        self._identifier = LanguageIdentifier.from_model_file(MODEL_FILE, norm_probs=True)

    def detect(self, text: str) -> LanguageResult:
        letters = [ch for ch in text if ch.isalpha()]
        if not letters:
            return LanguageResult(None, True)
        foreign_script = sum(not _latin_or_cyrillic(ch) for ch in letters) / len(letters)
        code, confidence = self._identifier.classify(text)
        code = ALIASES.get(code, code)
        if foreign_script > 0.5:
            return LanguageResult(code, False)
        if len(letters) < MIN_LETTERS or confidence < CONFIDENCE:
            return LanguageResult(code if confidence >= CONFIDENCE else None, True)
        return LanguageResult(code, code in SUPPORTED)


def _latin_or_cyrillic(ch: str) -> bool:
    name = unicodedata.name(ch, "")
    return name.startswith(("LATIN", "CYRILLIC"))
