"""Türkçe küfür/hakaret sözlüğü: modelin kaçırdığı argo için yüksek kesinlikli tamamlayıcı sinyal.

Detoxify çok dilli modeli Türkçeyi İngilizce verinin makine çevirisinden öğrendiği için yerel argoyu
(ör. "amına koyayım", "puşt", "göt") neredeyse sıfır skorlar. Sözlük yalnızca belirsizliği düşük
biçimleri içerir; eşleşme ilgili kategori skorunu tabana çeker:

    high    → 0.97 (kaldırma bandı): açık küfür, cinsel hakaret, nefret söylemi kalıpları
    medium  → 0.60 (inceleme bandı): bağlama göre değişebilen hakaretler

Türkçe harfler ASCII'ye katlanmaz ("sıkıntı" → "sikinti", İngilizce "got" → "göt" yanlış eşleşmesin);
yalnızca harf büyüklüğü (Türkçe kuralları), leetspeak ve uzatılmış harfler normalize edilir.
Kalıplar sözcük sınırına bağlıdır ("Sikke", "Amasya", "memento" eşleşmez).
"""

from __future__ import annotations

import re
from dataclasses import dataclass

HIGH = 0.97
MEDIUM = 0.60

_LEET = str.maketrans({"0": "o", "1": "i", "3": "e", "4": "a", "5": "s", "7": "t", "@": "a", "$": "s", "€": "e"})

# (kategori, şiddet, düzenli ifade) — ifadeler normalize edilmiş metinde sözcük sınırıyla aranır.
_PATTERNS: list[tuple[str, float, str]] = [
    # Açık küfür (obscene). "sik" kökü yalnızca "i" ile: "sık sık", "sıkıştı", "sıkıntı" eşleşmez.
    ("obscene", HIGH, r"am[ıi]n[aı]\s*(koy|koya|koyay|koyim|koyam|koyar)\w*|amk|amq|aq"),
    ("obscene", HIGH, r"sikt[iı]r\w*|sikt[iı]m\w*|siker[iı]m\w*|sikey[iı]m\w*|sikerler\w*|sikt[iı][gğ][iı]m\w*"),
    ("obscene", HIGH, r"sik|sikik\w*|sikiş\w*|sikis\w*|sikim\w*"),
    ("obscene", HIGH, r"anan[ıi]\s*(sik|sat|s[ıi]key)\w*|bac[ıi]n[ıi]\s*sik\w*"),
    ("obscene", HIGH, r"yarra[kğg]\w*|yarak\w*"),
    ("obscene", MEDIUM, r"taşşak\w*|taşak\w*|daşşak\w*"),
    ("obscene", MEDIUM, r"göt|götü|götün|götüne|götünü|götten|götlek\w*|götveren\w*"),
    ("obscene", MEDIUM, r"bok|boktan|boka|boku|bokun"),
    # Hakaret (insult)
    ("insult", HIGH, r"orospu\w*|orspu\w*|oç|o\.ç|piç|piçler\w*|piçin|pezevenk\w*|kahpe\w*|kaltak\w*"),
    ("insult", HIGH, r"puşt\w*|pust|yavşak\w*|yavsak\w*|gavat\w*|kevaşe\w*|şıllık\w*"),
    ("insult", MEDIUM, r"şerefsiz\w*|serefsiz\w*|haysiyetsiz\w*|namussuz\w*"),
    ("insult", MEDIUM, r"gerizekal[ıi]\w*|geri\s+zekal[ıi]\w*|beyinsiz\w*|dangalak\w*"),
    ("insult", MEDIUM, r"aşağılık\s+(herif|adam|kadın|biri)\w*|mal\s+m[ıi]s[ıi]n\w*|(it|köpek|hayvan)\s+herif\w*"),
    # Cinsel içerik (sexual_explicit). "amin" (dua) eşleşmez; yalnızca "ı" ile yazılan biçimler.
    ("sexual_explicit", HIGH, r"amın|amını|amcık\w*|amcik\w*|porno\w*"),
    ("sexual_explicit", MEDIUM, r"memeler\w*|memeleri\w*|sevişt\w*|seviştik"),
    # Kimlik saldırısı (identity_attack): slur ve grup + aşağılama kalıpları
    ("identity_attack", HIGH, r"ibne\w*"),
    (
        "identity_attack",
        MEDIUM,
        r"(kürt|ermeni|rum|yahudi|arap|suriyeli|alevi|çingene|kadın)\w*\s+(defol|pis|it|köpek|hain|terörist)\w*",
    ),
]

_COMPILED = [
    (category, severity, re.compile(rf"(?<!\w)(?:{pattern})(?!\w)")) for category, severity, pattern in _PATTERNS
]
_REPEATS = re.compile(r"(\w)\1{2,}")


_TOKENS = re.compile(r"\S+")


def _deleet(token: str) -> str:
    # Yalnızca en az iki harf taşıyan sözcükte: "s1kt1r" → "siktir", ama "$1k" (para) dokunulmaz.
    return token.translate(_LEET) if sum(ch.isalpha() for ch in token) >= 2 else token


def normalize(text: str) -> str:
    """Türkçe küçük harf + leetspeak + uzatılmış harfler ("siiiktir" → "siktir")."""
    lowered = text.replace("I", "ı").replace("İ", "i").lower()
    return _REPEATS.sub(r"\1", _TOKENS.sub(lambda m: _deleet(m.group()), lowered))


@dataclass(frozen=True)
class LexiconMatch:
    category: str
    severity: float


def find(text: str) -> list[LexiconMatch]:
    normalized = normalize(text)
    best: dict[str, float] = {}
    for category, severity, pattern in _COMPILED:
        if severity > best.get(category, 0.0) and pattern.search(normalized):
            best[category] = severity
    return [LexiconMatch(category, severity) for category, severity in best.items()]


def apply(scores: dict[str, float], matches: list[LexiconMatch]) -> dict[str, float]:
    """Eşleşen kategori ve genel ``toxicity`` skorunu sözlük tabanına çeker (model skoru düşürülmez)."""
    merged = dict(scores)
    for match in matches:
        merged[match.category] = max(merged.get(match.category, 0.0), match.severity)
        merged["toxicity"] = max(merged.get("toxicity", 0.0), match.severity)
    return merged
