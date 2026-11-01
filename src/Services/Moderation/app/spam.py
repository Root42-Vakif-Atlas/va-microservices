"""Spam / reklam kuralları: anıda iletişim bilgisi, tanıtım ve dolandırıcılık kalıpları.

Zararlılık modeli (Detoxify) reklamı "temiz" sayar ("En uygun kredi için arayın 0555…" 0,005); bağlam skoru da eserin
adını anan reklamı ilgili bulur ("Ulu Cami'nin yanındaki pansiyonumuz…"). Bu yüzden spam ayrı, yüksek kesinlikli
kural sinyalidir; karar katmanı bağlamdan önce uygular. Skor sözlükteki gibi iki seviyelidir:

    high    → 0.97 (kaldırma bandı): kesin spam kalıbı (bahis, takipçi satışı, "para kazan", abone ol…) veya
              iletişim bilgisi + ticari teklif birlikte
    medium  → 0.60 (inceleme bandı): yalnızca iletişim bilgisi (bağlantı, telefon, e-posta, @hesap, mesajlaşma
              uygulaması) — kişisel veri de olabilir, insan bakar

Ticari sözcük tek başına sinyal değildir ("bilet fiyatı uygundu" meşru bir anıdır). Kalıplar sözcük sınırına
bağlıdır; anahtar sözcükler sözlükle aynı normalizasyondan (Türkçe küçük harf, leetspeak, uzatılmış harfler),
iletişim kalıpları yalnızca küçük harfe çevrilmiş metinden aranır (rakamlar leetspeak'e çevrilmesin).
"""

from __future__ import annotations

import re
from dataclasses import dataclass, field

from app.lexicon import HIGH, MEDIUM, normalize

CONTACT = "contact"
PROMOTION = "promotion"
SCAM = "scam"

_CONTACT_PATTERNS = [
    # Bağlantı: şema, www, kısaltıcı ve yaygın üst alan adları ("vgm.gov.tr" de sayılır: anıda adres gereksizdir).
    r"https?://\S+|www\.\S+",
    # "me", "co", "tv" gibi kısa adlar yok: boşluksuz cümle sonu ("geldik.Me…") bağlantı sanılmasın.
    r"[a-z0-9-]+(?:\.[a-z0-9-]+)*\.(?:com|net|org|info|biz|xyz|io|ly|link|site|online|shop|store|bet|tr)(?:/\S*)?",
    r"(?:bit\.ly|t\.me|wa\.me|tinyurl\.com|goo\.gl)/\S*",
    # E-posta ve sosyal hesap (@kullanıcı; "@" tek başına veya e-posta içinde sayılmaz).
    r"[\w.+-]+@[\w-]+\.[\w.]+",
    r"(?<![\w@])@[a-z0-9_.]{3,30}",
    # Telefon: Türkiye cep/sabit (0 veya +90 önekli) ve uluslararası; boşluk, nokta, tire ayraçlı.
    r"(?:\+?90[\s.-]?|0)?\(?[2-5]\d{2}\)?[\s.-]?\d{3}[\s.-]?\d{2}[\s.-]?\d{2}",
    r"\+\d{1,3}[\s.-]?\d{2,4}[\s.-]?\d{3,4}[\s.-]?\d{2,4}",
    # Mesajlaşma / profil yönlendirmesi.
    r"whats\s?app\w*|telegram\w*|wp\s+hatt\w*|dm(?:'?(?:den|dan|e|a))?\s+(?:at|yaz|gel)\w*|dm\s+me|"
    r"mesaj\s+at\w*|özelden\s+yaz\w*|iletişim\s+için|"
    r"profilim\w*\s+(?:link|bağlantı)\w*|link\s+in\s+(?:my\s+)?bio|bio\w*\s*(?:daki|deki)\s+link\w*",
]

# Tek başına kaldırmaya yeten kalıplar: anıda meşru kullanımı yok.
_SCAM_PATTERNS = [
    r"(?:kaçak\s+|canlı\s+)?bahis\s*(?:sitesi|siteleri|oyna\w*)|deneme\s+bonus\w*|bedava\s+bonus\w*|casino\w*|"
    r"slot\s+oyun\w*|iddaa\s+tahmin\w*|kupon\s+paylaş\w*",
    r"takip[cç]i\s+(?:sat\w*|al\w*|kas\w*|hile\w*)|beğeni\s+(?:sat\w*|al\w*)|takipleş\w*|geri\s+takip\w*",
    r"abone\s+ol(?:un|ur\s+musunuz|mayı\s+unutmayın)\w*|kanal\w*\s+abone\w*",
    r"(?:günde|haftada|ayda|evden)\s+(?:\d[\d.]*\s*(?:tl|lira|dolar|\$|€)\s+)?(?:para\s+)?kazan\w*|"
    r"para\s+kazanmak\s+ister\w*|pasif\s+gelir\w*|evden\s+çalış\w*",
    r"(?:bitcoin|kripto\w*|forex|coin)\w*\s+(?:\S+\s+){0,4}(?:kazan\w*|kat\s+kazan\w*|yatırım\w*|sinyal\w*)",
    r"escort\w*|eskort\w*",
    r"get\s+rich|crypto\s+(?:signal|profit|investment)\w*|buy\s+(?:cheap\s+)?(?:followers|likes)|"
    r"(?:free\s+)?gift\s+card|giveaway|click\s+(?:the\s+)?link|subscribe\s+to\s+my|online\s+casino|betting\s+site\w*",
]

# İletişim bilgisiyle birlikteyse kaldırma: ticari teklif.
_PROMOTION_PATTERNS = [
    r"indirim\w*|kampanya\w*|fırsat\w*|rezervasyon\w*|satılık|kiralık|sipariş\w*|fiyat\w*|ücretsiz|bedava|"
    r"uygun\s+fiyat\w*|kredi\w*|faiz\w*|servis\w*|kurs\w*|eğitim\w*|kayıt\s+ol\w*|hemen\s+ara\w*|bilgi\s+için|"
    r"otel\w*|pansiyon\w*|tur\s+(?:paket\w*|fiyat\w*)|%\s?\d+|\d+\s?%|\d+\s?(?:tl|₺)",
    r"discount|sale|booking|order\s+now|cheap|promo\w*|offer\w*|deal\w*",
]


def _compile(patterns: list[str]) -> list[re.Pattern[str]]:
    return [re.compile(rf"(?<!\w)(?:{pattern})(?!\w)") for pattern in patterns]


_CONTACT = _compile(_CONTACT_PATTERNS)
_SCAM = _compile(_SCAM_PATTERNS)
_PROMOTION = _compile(_PROMOTION_PATTERNS)


@dataclass(frozen=True)
class SpamResult:
    score: float
    signals: list[str] = field(default_factory=list)


def _lower(text: str) -> str:
    return text.replace("I", "ı").replace("İ", "i").lower()


def check(text: str) -> SpamResult:
    lowered = _lower(text)
    normalized = normalize(text)
    contact = any(p.search(lowered) for p in _CONTACT)
    scam = any(p.search(normalized) or p.search(lowered) for p in _SCAM)
    promotion = any(p.search(normalized) or p.search(lowered) for p in _PROMOTION)
    signals = [name for name, hit in ((CONTACT, contact), (PROMOTION, promotion and contact), (SCAM, scam)) if hit]
    if scam or (contact and promotion):
        return SpamResult(HIGH, signals)
    if contact:
        return SpamResult(MEDIUM, signals)
    return SpamResult(0.0, [])
