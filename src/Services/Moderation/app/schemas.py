"""HTTP sözleşmesi (camelCase JSON, .NET servisleriyle aynı biçim)."""

from __future__ import annotations

import base64
import binascii

from pydantic import BaseModel, ConfigDict, Field, field_validator, model_validator
from pydantic.alias_generators import to_camel

MAX_ITEMS = 5
MAX_TEXT = 2000
# PhotoRules.MaxPhotos / MaxBytes ile aynı (Memories küçük resmi gönderir, ~50–150 KB).
MAX_IMAGES = 4
MAX_IMAGE_BYTES = 5 * 1024 * 1024


class _Camel(BaseModel):
    model_config = ConfigDict(alias_generator=to_camel, populate_by_name=True)


class ClassifyItem(_Camel):
    id: str = Field(min_length=1, max_length=64)
    text: str = Field(min_length=1, max_length=MAX_TEXT)


class ClassifyRequest(_Camel):
    items: list[ClassifyItem] = Field(min_length=1, max_length=MAX_ITEMS)
    # Verilirse bağlam esere göre de ölçülür; yoksa yalnızca anı türü prototiplerine.
    monument_id: str | None = Field(default=None, max_length=120)


class ModelInfoResponse(_Camel):
    name: str
    version: str
    categories: list[str]


class VisionInfoResponse(_Camel):
    name: str
    version: str
    nsfw: str
    embedding: str


class ReferencesInfoResponse(_Camel):
    version: str
    works: int
    images: int


class ContextInfoResponse(_Camel):
    """Bağlam modeli (cümle gömmesi) ve bağlam indeksi (eser metinleri + anı türü prototipleri)."""

    name: str
    version: str
    embedding: str
    index: str
    works: int
    prototypes: int
    damage_prototypes: int


class ServiceInfoResponse(ModelInfoResponse):
    """``GET /v1/model``: metin modeli (eski alanlar) + görsel modeller, referans indeksi ve bağlam modeli."""

    vision: VisionInfoResponse | None
    references: ReferencesInfoResponse | None
    context: ContextInfoResponse | None


class LexiconHit(_Camel):
    category: str
    severity: float


class ClassifyResult(_Camel):
    id: str
    scores: dict[str, float]
    lexicon: list[LexiconHit]
    language: str | None
    language_supported: bool
    # Spam kuralları: 0 / 0,60 (inceleme) / 0,97 (kaldırma); sinyaller contact · promotion · scam.
    spam: float
    spam_signals: list[str]
    # Eserle bağlam: max(esere, anı türüne) kosinüs; bağlam modeli yoksa null. monumentRelevance: eser indekste
    # yoksa null.
    relevance: float | None
    monument_relevance: float | None


class DamageResult(_Camel):
    """Hasar açıklaması: hasar tarifine benzerlik ve tür başına uyum (kosinüs; tür uyumu yalnızca bilgi)."""

    relevance: float
    categories: dict[str, float]


class AssessTextResult(ClassifyResult):
    # Bağlam modeli veya hasar prototipleri yoksa null.
    damage: DamageResult | None


class ClassifyResponse(_Camel):
    model: ModelInfoResponse
    context: ContextInfoResponse | None
    results: list[ClassifyResult]


class ImageItem(_Camel):
    id: str = Field(min_length=1, max_length=64)
    # Standart base64 (RFC 4648); çözülmüş boyut ≤ 5 MB.
    data: bytes

    @field_validator("data", mode="before")
    @classmethod
    def _decode(cls, value: object) -> bytes:
        if not isinstance(value, str) or len(value) > (MAX_IMAGE_BYTES // 3 + 1) * 4:
            raise ValueError("base64 image up to 5 MB expected")
        try:
            data = base64.b64decode(value, validate=True)
        except binascii.Error as exc:
            raise ValueError("invalid base64") from exc
        if not data:
            raise ValueError("empty image")
        return data


class ModerateRequest(_Camel):
    """Bir anının tamamı: metinler (anı + fotoğraf betimleri) ve fotoğraflar; ``monumentId`` eser benzerliği ve
    metin bağlamı içindir."""

    monument_id: str | None = Field(default=None, max_length=120)
    texts: list[ClassifyItem] = Field(default_factory=list, max_length=MAX_ITEMS)
    images: list[ImageItem] = Field(default_factory=list, max_length=MAX_IMAGES)

    @model_validator(mode="after")
    def _not_empty(self) -> ModerateRequest:
        if not self.texts and not self.images:
            raise ValueError("at least one text or image is required")
        return self


class ImageResult(_Camel):
    id: str
    readable: bool
    nsfw: float | None
    # Eserin referanslarının merkezine kosinüs benzerliği; eserin referansı yoksa null.
    similarity: float | None
    references: int
    # İndeksteki tek bir katalog görseline en yüksek kosinüs (≥ ~0,94: katalog görselinin kopyası olabilir).
    catalog_copy: float | None = None


class ModerateResponse(_Camel):
    model: ModelInfoResponse
    vision: VisionInfoResponse | None
    references: ReferencesInfoResponse | None
    context: ContextInfoResponse | None
    texts: list[ClassifyResult]
    images: list[ImageResult]


class AssessReportRequest(ModerateRequest):
    """Sivil Koruma hasar bildirimi: açıklama (+ fotoğraf betimleri) ve fotoğraflar; eser anahtarı zorunlu."""

    monument_id: str = Field(min_length=1, max_length=120)
    texts: list[ClassifyItem] = Field(min_length=1, max_length=MAX_ITEMS)


class AssessReportResponse(_Camel):
    model: ModelInfoResponse
    vision: VisionInfoResponse | None
    references: ReferencesInfoResponse | None
    context: ContextInfoResponse | None
    texts: list[AssessTextResult]
    images: list[ImageResult]
