import pytest

from app.language import LanguageDetector


@pytest.fixture(scope="module")
def detector() -> LanguageDetector:
    return LanguageDetector()


@pytest.mark.parametrize(
    "text",
    [
        "Süleymaniye Camii'ni ailemle ziyaret ettik, avlusu çok huzurluydu.",
        "Türbede dua ettik, ruhları şad olsun.",
        "We visited with our kids, the tiles were stunning.",
        "Nous avons visité la mosquée avec nos enfants.",
        "harika",
        "👍👍",
    ],
)
def test_supported_or_too_short_to_tell(detector: LanguageDetector, text: str) -> None:
    assert detector.detect(text).supported


@pytest.mark.parametrize(
    "text",
    [
        "Wir haben die Moschee besucht, sie war wirklich sehr schön.",
        "زرنا المسجد مع عائلتي وكان جميلا جدا",
        "我们和家人一起参观了清真寺",
    ],
)
def test_unsupported_language_is_flagged(detector: LanguageDetector, text: str) -> None:
    assert not detector.detect(text).supported
