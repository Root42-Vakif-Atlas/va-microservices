"""Model + sözlük + dil + spam + eser bağlamı sinyallerini (metin) ve NSFW + eser benzerliği sinyallerini (görsel)
birleştirir.
Karar (onay/inceleme/kaldırma) burada verilmez; eşik politikası Memories servisinin domain'indedir
(``ModerationPolicy``)."""

from __future__ import annotations

import contextlib
from dataclasses import dataclass

from app import lexicon, spam
from app.context import ContextIndex, DamageSignal, Relevance, TextEmbedder
from app.language import LanguageDetector, LanguageResult
from app.model import Scorer
from app.references import Match, ReferenceIndex
from app.spam import SpamResult
from app.vision import UnreadableImage, VisionModels, decode


@dataclass(frozen=True)
class Classification:
    scores: dict[str, float]
    lexicon: list[lexicon.LexiconMatch]
    language: LanguageResult
    spam: SpamResult
    # None: bağlam modeli veya indeksi yüklü değil (karar katmanı bağlam kuralını uygulamaz).
    relevance: Relevance | None = None
    # Yalnızca hasar bildirimi değerlendirmesinde (``damage=True``) ve indekste hasar prototipleri varsa.
    damage: DamageSignal | None = None


@dataclass(frozen=True)
class ImageClassification:
    # readable=False: görsel çözülemedi; skorlar boş, karar katmanı incelemeye bırakır.
    readable: bool
    nsfw: float | None
    similarity: float | None
    references: int
    catalog_copy: float | None = None


class VisionUnavailable(RuntimeError):
    """Görsel modelleri yüklenmedi (yalnızca testlerde ve metin-only kurulumda)."""


class ModerationEngine:
    def __init__(
        self,
        scorer: Scorer,
        languages: LanguageDetector | None,
        vision: VisionModels | None = None,
        references: ReferenceIndex | None = None,
        embedder: TextEmbedder | None = None,
        context: ContextIndex | None = None,
    ) -> None:
        self.scorer = scorer
        self.vision = vision
        self.references = references
        # Bağlam yalnızca model ve indeks birlikteyken hesaplanır.
        self.embedder = embedder if context is not None else None
        self.context = context if embedder is not None else None
        self._languages = languages

    @property
    def info(self):
        return self.scorer.info

    def classify(self, texts: list[str], monument_id: str | None = None, damage: bool = False) -> list[Classification]:
        relevance: list[Relevance | None] = [None] * len(texts)
        damages: list[DamageSignal | None] = [None] * len(texts)
        if self.embedder is not None and self.context is not None:
            vectors = self.embedder.embed(texts)
            relevance = list(self.context.relevance(vectors, monument_id))
            if damage and (signals := self.context.damage(vectors)) is not None:
                damages = list(signals)
        results = []
        scored = zip(texts, self.scorer.score(texts), relevance, damages, strict=True)
        for text, scores, context, damage_signal in scored:
            matches = lexicon.find(text)
            language = self._languages.detect(text) if self._languages else LanguageResult(None, True)
            results.append(
                Classification(
                    lexicon.apply(scores, matches), matches, language, spam.check(text), context, damage_signal
                )
            )
        return results

    def classify_images(self, images: list[bytes], monument_id: str | None) -> list[ImageClassification]:
        if self.vision is None:
            raise VisionUnavailable
        decoded = {}
        for i, data in enumerate(images):
            with contextlib.suppress(UnreadableImage):
                decoded[i] = decode(data)
        nsfw = dict(zip(decoded, self.vision.nsfw(list(decoded.values())), strict=True)) if decoded else {}
        vectors = dict(zip(decoded, self.vision.embed(list(decoded.values())), strict=True)) if decoded else {}

        results = []
        for i in range(len(images)):
            if i not in decoded:
                results.append(ImageClassification(False, None, None, 0))
                continue
            match = self.references.match(vectors[i], monument_id) if self.references else Match(None, 0)
            results.append(ImageClassification(True, nsfw[i], match.similarity, match.references, match.copy))
        return results
