import pytest

from app import spam


@pytest.mark.parametrize(
    "text",
    [
        "En uygun kredi faizleri için hemen arayın 0555 123 45 67",
        "Otelimizde %40 indirim, rezervasyon için WhatsApp'tan yazın",
        "Ulu Cami'nin yanındaki pansiyonumuzda yer var: 0224 555 66 77",
        "Kombi servisi, uygun fiyat. Bilgi: info@kombiservis.com",
        "Satılık daire, fiyat için mesaj atın.",
        "Tur paketlerimiz için www.ornek-tur.com",
        "Deneme bonusu veren siteler burada",
        "Canlı casino ve slot oyunları",
        "Instagram takipçi satın al",
        "Takip edin takipleşelim",
        "Kanalıma abone olun!",
        "Evden çalışarak günde 5000 TL kazanın",
        "Bitcoin ile bir haftada 10 kat kazan",
        "Get rich quick, click the link in my bio",
        "Buy cheap followers today",
        "b4his siteleri için deneme b0nusu",
    ],
)
def test_promotion_and_scam_are_rejectable(text: str) -> None:
    result = spam.check(text)
    assert result.score == spam.HIGH
    assert result.signals


@pytest.mark.parametrize(
    "text",
    [
        "Görevliye ulaşmak için 0532 111 22 33 numarasını aradık, kapıyı açtı.",
        "Daha fazla bilgi vgm.gov.tr adresinde.",
        "Fotoğraflarımı @gezgin_ayse hesabımda paylaştım.",
        "Telegram grubumuzda buluşup birlikte geldik.",
    ],
)
def test_contact_alone_goes_to_review(text: str) -> None:
    result = spam.check(text)
    assert result.score == spam.MEDIUM
    assert result.signals == [spam.CONTACT]


@pytest.mark.parametrize(
    "text",
    [
        "Bursa Ulu Camii'nin şadırvanında dinlendik.",
        "Caminin açılışı 1399 yılında bir cuma günü yapılmış.",
        "Bilet 50 TL idi, öğrenci indirimi yoktu.",
        "Fiyatlar çok pahalı, öğrenci indirimi bile yok.",
        "Otel odamızdan minare görünüyordu, sabah ezanıyla uyandık.",
        "Rezervasyon yaptırıp hamama geldik, kurnalar çok eskiydi.",
        "Çocukken burada Kuran kursuna giderdim.",
        "2019 ve 2020 yıllarında iki kez geldik.",
        "Restorasyon %90 tamamlanmış görünüyor.",
        "Kervansaray eskiden ticaret yolunun en önemli durağıymış.",
        "Mevlâna'yı ziyaret edip Mesnevi'den beyitler dinledik.",
        "Sıkıntılı bir gündü ama burası iyi geldi.",
        "geldik.Mescit çok güzeldi",
        "We booked a guided tour and loved it.",
    ],
)
def test_legitimate_memories_are_not_spam(text: str) -> None:
    assert spam.check(text) == spam.SpamResult(0.0, [])
