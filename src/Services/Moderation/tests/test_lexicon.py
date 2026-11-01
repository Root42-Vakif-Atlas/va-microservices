import pytest

from app import lexicon


@pytest.mark.parametrize(
    ("text", "category", "severity"),
    [
        ("Amına koyayım böyle restorasyonun.", "obscene", lexicon.HIGH),
        ("Ortalığın Amına koymalık bir gün", "obscene", lexicon.HIGH),
        ("hay aq", "obscene", lexicon.HIGH),
        ("SİKTİR git", "obscene", lexicon.HIGH),
        ("s1kt1r git", "obscene", lexicon.HIGH),
        ("siiiiktir", "obscene", lexicon.HIGH),
        ("Ananı satayım böyle işin.", "obscene", lexicon.HIGH),
        ("Puşt herif bilet kesmedi bize.", "insult", lexicon.HIGH),
        ("Yavşak görevli kapıyı kapattı.", "insult", lexicon.HIGH),
        ("orospu çocuğu", "insult", lexicon.HIGH),
        ("Göt kadar bahçeye bilet istiyorlar.", "obscene", lexicon.MEDIUM),
        ("Bu caminin imamı şerefsiz bir adam.", "insult", lexicon.MEDIUM),
        ("Bu fotoğraftaki kızın memeleri harika.", "sexual_explicit", lexicon.MEDIUM),
        ("Kürtler defolsun", "identity_attack", lexicon.MEDIUM),
    ],
)
def test_matches_turkish_profanity(text: str, category: str, severity: float) -> None:
    matches = {m.category: m.severity for m in lexicon.find(text)}
    assert matches.get(category) == severity


@pytest.mark.parametrize(
    "text",
    [
        "Sık sık geliyoruz, çok seviyoruz.",
        "Kalabalıkta sıkıştık ama değdi.",
        "Sıkıntılı bir gündü, avlu iyi geldi.",
        "Sikke müzesinde eski paraları gördük.",
        "Amasya'daki kaya mezarlarını gezdik.",
        "Allah kabul etsin, amin.",
        "Amcamla birlikte geldik.",
        "Alçak tavanlı bir hamam.",
        "Öküz arabasıyla taşınmış taşlar.",
        "I got the tickets online, cost $1k for the whole family.",
        "We took a pic at the gate.",
        "Memento mori yazısı vardı.",
        "Kürtçe ve Türkçe konuşan çocuklar oynuyordu.",
        "Malımızı mülkümüzü vakfetmişler.",
        "Bokser köpeğimizle avluda gezdik.",
    ],
)
def test_ignores_innocent_text(text: str) -> None:
    assert lexicon.find(text) == []


def test_apply_raises_category_and_toxicity_without_lowering() -> None:
    scores = {"toxicity": 0.99, "obscene": 0.1, "insult": 0.2}
    merged = lexicon.apply(scores, [lexicon.LexiconMatch("obscene", lexicon.MEDIUM)])
    assert merged == {"toxicity": 0.99, "obscene": lexicon.MEDIUM, "insult": 0.2}


def test_normalize_uses_turkish_casing() -> None:
    assert lexicon.normalize("IŞIK İzmir") == "ışık izmir"
