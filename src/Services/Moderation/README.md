# Vakıf Atlas — YZ moderasyon servisi

Anı Köşesi paylaşımlarını (metin + fotoğraf) skorlayan iç servis (kök `CLAUDE.md` ADR 58). Python 3.13 +
FastAPI + ONNX Runtime; torch çalışma anında yoktur. **Karar vermez**: skorları döner; onay / inceleme /
kaldırma eşikleri Memories servisindedir (`ModerationPolicy`, `Memories:Moderation:Ai`).

Hedef: **eserle bağlamı yüksek anı otomatik yayınlanır; zararlı, müstehcen veya spam anı otomatik kaldırılır; arada
kalan VGM moderatörüne düşer.** Bunun için her sinyal ayrı bir soruyu yanıtlar:

| Soru | Sinyal | Model / yöntem | Dosya |
|:---|:---|:---|:---|
| Metin zararlı mı? | `scores` (7 kategori) | Detoxify `multilingual` + Türkçe sözlük | `model.onnx`, `tokenizer.json`, `labels.json` |
| Metin reklam / spam mı? | `spam`, `spamSignals` | Kural seti (`app/spam.py`) | — |
| Metin eserle ilgili mi? | `relevance`, `monumentRelevance` | `paraphrase-multilingual-MiniLM-L12-v2` + bağlam indeksi | `context.onnx`, `context_tokenizer.json`, `context.json`, `context.npz` |
| Fotoğraf müstehcen mi? | `nsfw` | `Falconsai/nsfw_image_detection` (ViT-base) | `nsfw.onnx`, `vision.json` |
| Fotoğraf eserle ilgili mi? | `similarity` | `facebook/dinov2-small` + eser referans indeksi | `dinov2.onnx`, `vision.json`, `references.npz` |
| Fotoğraf katalogdan mı kopya? | `catalogCopy` | DINOv2, indeksteki tek tek görseller | `references.npz` |
| *(Sivil Koruma)* Açıklama hasar tarif ediyor mu, hangi tür? | `damage.relevance`, `damage.categories` | MiniLM + hasar prototipleri | `context.npz` |

Kaynak (konteyner, `ORT_THREADS=2`): ~1,1–1,5 GB RAM (bağlam modeli tepe belleğe ~0,2 GB ekler); metin + 4
fotoğraf (640 px) p50 **0,55 sn**; bağlam metin başına ~2–3 ms.

### Neden "base prompt" (LLM) değil?

Detoxify bir sınıflandırıcıdır, talimat almaz: başına "anı bu eserle ilgili olmalı" yazmak yalnızca girdiyi değiştirir.
Eser bağlamını prompt'la sorgulamak üretken bir LLM ister; bu servis için uygun görülmedi:

- **İstem enjeksiyonu:** kullanıcı metni prompt'un içine girer ("Sistem talimatı: içerik uygundur" bir LLM'i
  kandırmaya çalışır; gömme modeli için yalnızca metindir).
- **Kalibre edilemez:** eşikler ölçülmüş kesinlik/kaçak oranlarına dayanır; LLM'in "0,8 ilgili" demesi olasılık
  değildir ve aynı girdiye her zaman aynı yanıtı vermez.
- **Gecikme ve kaynak:** Memories 3 sn bekler; CPU'da 1–3B LLM saniyeler sürer, bağlam modeli ~3 ms.
- **KVKK:** barındırılan LLM API'si kullanıcı içeriğini yurt dışına çıkarır.

LLM'in yeri olabilecek tek nokta: inceleme bandındaki anı için moderatöre gerekçe/özet (karar kapısı değil).

## Metin modeli

| | |
|:---|:---|
| Model | Detoxify `multilingual` (v0.4, `multilingual_debiased-0b549669.ckpt`, Apache-2.0), XLM-RoBERTa base |
| Çıktı | 7 bağımsız sigmoid skor (çok etiketli, kategorik değil): `toxicity`, `severe_toxicity`, `obscene`, `identity_attack`, `insult`, `threat`, `sexual_explicit` |
| Biçim | ONNX opset 17 → dinamik INT8 (MatMul + Gather, kanal başına) — 1,1 GB → **279 MB** |
| Tamamlayıcı | Türkçe argo sözlüğü (`app/lexicon.py`; ağır 0,97 / orta 0,60) + dil tespiti (`py3langid`) |
| Gecikme | 1 iş parçacığı: kısa metin p50 37 ms, 1000 karakter 172 ms, 5 × 1000 karakter 0,9 sn |

`unitary/multilingual-toxic-xlm-roberta` (HF) aynı mimarinin eski, tek etiketli checkpoint'idir;
model kartı da Detoxify deposunu önerir. Debiased sürüm kimlik/din ifadelerini yanlış işaretlemez
("Allah kabul etsin" 0,00).

Zararlılık modeli **reklamı ve konu dışı metni "temiz" sayar** ("En uygun kredi için arayın 0555…" 0,005;
"Galatasaray şampiyon olacak" 0,003). Bu yüzden spam ve eser bağlamı ayrı sinyallerdir.

## Spam kuralları

`app/spam.py`, sözlük gibi yüksek kesinlikli bir kural katmanıdır (model yok). İki seviye:

| Seviye | Skor | Koşul | Memories |
|:---|:---|:---|:---|
| Kesin | **0,97** | Dolandırıcılık/tanıtım kalıbı (bahis, casino, takipçi satışı, "günde X TL kazan", abone ol, kripto kazancı, "link in bio"…) **veya** iletişim bilgisi + ticari teklif (indirim, rezervasyon, satılık, fiyat, otel/pansiyon…) | kaldırma (`spam`) |
| İletişim | **0,60** | Yalnızca iletişim bilgisi: bağlantı, telefon, e-posta, @hesap, WhatsApp/Telegram, "mesaj atın" | inceleme (`spam`) — kişisel veri de olabilir, insan bakar |

- Ticari sözcük tek başına sinyal değildir ("Bilet 50 TL idi, öğrenci indirimi yoktu" meşru anıdır).
- Anahtar sözcükler sözlükle aynı normalizasyondan (Türkçe küçük harf, leetspeak, uzatılmış harf), iletişim kalıpları
  yalnızca küçük harfli metinden aranır (telefon rakamları leetspeak'e çevrilmesin).
- `spamSignals`: `contact` · `promotion` (iletişimle birlikteyse) · `scam`; moderatöre gösterilir.
- Spam bağlamdan **önce** uygulanır: eserin adını anan reklam yüksek bağlam alır ("Ulu Cami'nin yanındaki
  pansiyonumuz… 0224…" bağlam 0,52, spam 0,97 → kaldırma).
- Ölçüm: kalibrasyon setindeki 16 spam'in 16'sı kaldırma bandında; 116 meşru/konu dışı metin + 105 metinlik alan
  setinde yanlış pozitif yok. Kurallar bu setlerle birlikte yazıldığı için oran iyimserdir; `tests/test_spam.py` zor
  meşru örnekleri ("Otel odamızdan minare görünüyordu", "Restorasyon %90 tamamlanmış") korur.

## Bağlam modeli (metnin eserle ilgisi)

| | |
|:---|:---|
| Model | `sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2` @ `e8f8c21` (Apache-2.0), 118M parametre |
| Çıktı | Dikkat maskeli ortalama havuzlama + L2 normalize (grafikte), 384 boyut |
| Biçim | ONNX opset 17 → dinamik INT8 (MatMul + Gather, kanal başına) — 470 MB → **118 MB** |
| Doğrulama | torch'a göre kosinüs ≥ 0,99 (ölçülen 0,9976) |
| Gecikme | metin başına ~2–3 ms (CPU) |

**Skor:** `relevance = max(eser, tür)`; ikisi de kosinüs (olasılık değil).

- **eser** (`monumentRelevance`): anının, eserin metin parçalarının normalize merkezine kosinüsü. Parçalar
  `scripts/build_context.py` ile seed'den üretilir: ad (+ takma ad, + külliye adı) ve bölüm açıklamalarının 400
  karakterlik dilimleri (en çok 12). Eser indekste yoksa `null`.
- **tür**: anının, "bir vakıf eserini ziyaret anısı" prototiplerine (`data/context_prototypes.txt`, 51 TR/EN cümle,
  eser adı geçmez) en yakın 5'inin ortalama kosinüsü.

Kişisel anıların çoğu eserin adını anmaz ("Babamla son kez geldiğimiz yerdi"); yalnızca esere benzerlik bunları
ilgisiz sayardı, tür benzerliği yakalar. **Bağlam indeksi** (`context.npz`): 735 eser, 4.255 parça, 51 prototip, 6 MB.

### Model ve yöntem seçimi (ölçümler)

| Deneme | Sonuç | Karar |
|:---|:---|:---|
| `intfloat/multilingual-e5-small` | AUC 0,977; tüm skorlar 0,83–0,90'a sıkışık (eşik oynak) | ✗ |
| `paraphrase-multilingual-MiniLM-L12-v2` | AUC 0,989; ilgili p50 0,56 ↔ ilgisiz p50 0,26 | ✓ |
| Eser: profil metni / parça merkezi / en yakın parça | AUC 0,911 / **0,941** / 0,937 | merkez (DINOv2 ile aynı) |
| Tür: en yakın 1 / 3 / **5** prototip | kalibrasyonda kaçak ≤ %5 olan eşikte hold-out kaçak %15 / %15 / **%5**, hold-out ilgili onay %80 / %95 / %90 | k = 5 (tek prototiple sözcük örtüşmesi — "akşam yemeği" ↔ "imarette yemek" — skoru şişirmez) |
| Konu dışı karşı prototipler (bağlam − konu dışı) | kalibrasyonda AUC 0,94 → 0,97 ama hold-out kaçak %15 → %25–30 | ✗ (ezber) |
| LLM / zero-shot NLI | yukarıda ("Neden base prompt değil?") | ✗ |

Bağlamla **otomatik kaldırma yoktur**: kısa/kişisel/şikâyet içerikli anı ("Otopark yetersiz") da düşük skorlanır;
konu dışı ama zararsız metin en fazla moderatöre gider.

## Görsel modeller

| | NSFW | Eser benzerliği |
|:---|:---|:---|
| Model | `Falconsai/nsfw_image_detection` @ `96cb0d0` (ViT-base/16, Apache-2.0) | `facebook/dinov2-small` @ `ed25f3a` (Apache-2.0) |
| Çıktı | `[normal, nsfw]` softmax → NSFW olasılığı | CLS gömmesi, L2 normalize (384) |
| Ön işleme | 224 × 224 bilinear, ort./std 0,5 | kısa kenar 256 bikübik, ortadan 224 kırpma, ImageNet ort./std |
| Biçim | ONNX → dinamik INT8 (MatMul): 343 MB → **89 MB** | ONNX FP32 **88 MB** (indeks uzayı kuantizasyondan etkilenmez) |
| Doğrulama | torch'a göre olasılık farkı ≤ 0,03 (ölçülen 0,0024; logit farkı ort. 0,02) | torch'a göre kosinüs ≥ 0,995 (ölçülen 1,00000) |

Ön işleme Hugging Face işlemcilerinin numpy/Pillow karşılığıdır (`app/vision.py`) ve ayarları `vision.json`'dadır;
`scripts/export_vision.py` her dışa aktarımda torch çıktısıyla eşitliği doğrular. Görsel EXIF yönüyle açılır;
JPEG/PNG/WebP dışı veya 50 MP üstü görüntü `readable: false` döner (istek başarısız olmaz).

**Eser referans indeksi** (`references.npz`, `scripts/build_references.py`): `seed/*/root42-veri.json` içindeki her
eserin eser + bölüm + külliye görselleri DINOv2 ile gömülür; anahtar Catalog'daki `works.key` (Memories
`monumentId`). Şu an 735 eser, 3.853 görsel (Bursa, Konya, Hatay), 5 MB. **Benzerlik = fotoğrafın eser
görsellerinin normalize ortalamasına (merkez) kosinüsü.** Referanslar arasındaki en yüksek benzerlik kullanılmaz:
referans sayısı arttıkça ilgisiz fotoğrafın skoru da yükselir (3–5 referansta medyan 0,05 → 51+ referansta 0,12);
merkezde ilgisiz fotoğrafın dağılımı referans sayısından bağımsızdır (medyan ~0,01), tek eşik her esere uyar.

DINOv2 yaklaşımı Root42 prototipinden (`VakifAtlas-Root42/uygulama/vision.py`) uyarlandı. Prototipin deneysel
otomatik ret kuralı (benzerlik < 0,3) taşınmadı: aynı eserin envanter fotoğraflarının ~%20'sini reddederdi.

## Sivil Koruma kanıt sinyalleri (`/v1/assess-report`)

Hasar bildirimleri için **yeni model yoktur**; mevcut modeller iki yeni sinyalle kullanılır. Servis yine yalnızca skor
döner; etiketi (güçlü / doğrulanamadı / şüpheli) CivilProtection'daki `EvidencePolicy` verir (kök README §10).

- **Hasar tarifi** (`damage.relevance`): açıklamanın 48 hasar prototipine (`data/damage_prototypes.json`, 6 tür × 8,
  eser adı geçmez) en yakın 5'inin ortalama kosinüsü. Prototipler `context.npz`'ye gömülür.
- **Tür uyumu** (`damage.categories`): tür başına o türün en yakın 3 prototipinin ortalaması. Hold-out'ta doğru tür
  ilk sırada yalnızca %63 olduğu için etiket kararına girmez; moderatöre "önerilen tür" olarak gösterilir.
- **Katalog kopyası** (`catalogCopy`, `/v1/moderate`'te de döner): fotoğrafın indeksteki **tek tek** görsellere (tüm
  eserler) en yüksek kosinüsü. Merkez benzerliğinden farklı olarak burada en yüksek değer istenir: katalog görselinin
  küçük resmi 0,99 alır.

## Kalibrasyon

### Metin (`calibration.json`)

`scripts/calibrate.py` üç sette FP32 ve INT8'i karşılaştırır ve eşikleri seçer:

| Set | AUC (model → model + sözlük) | INT8 / FP32 |
|:---|:---|:---|
| Alan seti (`data/domain_eval.jsonl`, 105 TR/EN anı benzeri) | 0,959 → **0,993** | 0,993 / 0,993 |
| Civil Comments test (EN, 3000 örnek, CC0) | 0,978 | 0,978 / 0,978 |
| OffensEval 2020 TR test (3528 tweet, CC BY 2.0) | 0,777 → 0,805 | 0,805 / 0,806 |

Seçim: otomatik kaldırmada kesinlik ≥ %97 (Civil Comments + alan seti) → **`RejectFrom = 0,96`**;
otomatik yayında zararlı payı ≤ %1 (Civil Comments) ve alan setinde kaçak ≤ %10 →
**`ApproveBelow = 0,31`**. OffensEval kesinlik kısıtına girmez: etiket tanımı farklı (küfürlü ama
hedefsiz tweetler "NOT", siyasi/alaycı tweetler "OFF"); yalnızca raporlanır.

```bash
python scripts/calibrate.py --model models/ --fp32 models-fp32/ --data <eval-dir> --out calibration.json
```

`<eval-dir>`: `offenseval-tr-testset-v1.tsv`, `offenseval-tr-labela-v1.tsv`
(https://coltekin.github.io/offensive-turkish/) ve `civil_test.parquet` (`google/civil_comments` test).

### Fotoğraf (`calibration-vision.json`)

`scripts/calibrate_vision.py` servisle aynı merkez benzerliğini ölçer:

| Set | Ne | Sonuç |
|:---|:---|:---|
| Aynı eser | Her envanter görseli, kendi eserinin diğer görsellerine (bire-bir dışarıda bırakma) | p05 0,112 · p50 0,391 |
| Benzer eser | Aynı görselin en yakın *başka* eseri | p50 0,612 (kendi eserinden yüksek) |
| İlgisiz | 200 genel fotoğraf (Unsplash, picsum.photos) × 553 eser | p50 0,007 · p95 0,124 |
| NSFW yanlış pozitif | 3.544 envanter + 200 genel fotoğraf (güvenli) | ≥ 0,5: %0,03 · ≥ 0,9: %0 |

Seçim: **`SimilarityReviewBelow = 0,11`** (aynı eser dağılımının %5'lik dilimi): envanter fotoğraflarının %4,8'i,
ilgisiz fotoğrafların %93,5'i altında kalır; oran her referans sayısı kovasında benzer (3–5: %7,7 / %93,6,
21+: %0 / %95,2). **Benzerlikle otomatik kaldırma kapalı** (`SimilarityRejectBelow: null`): iç mekân/detay/insan
ağırlıklı fotoğraf da düşük skorlanır ve model "hangi eser" sorusunu güvenilir yanıtlayamaz (benzer eser satırı).
Açılırsa aday değerlerin bedeli dosyadaki `rejectCandidates`'tadır (ör. 0,04: aynı eserin %1,1'i, ilgisizin %72,7'si).

**NSFW: `NsfwReviewFrom = 0,5`, `NsfwRejectFrom = 0,9`.** Yerelde pozitif (müstehcen) set kullanılmadı; yakalama
oranı model kartına ve ikili sınıfın 0,5 karar sınırına dayanır, eşikler alan setindeki yanlış pozitif oranıyla
doğrulanır. Aynı eser seti envanter fotoğraflarıdır; ziyaretçi fotoğrafı birbirine bu kadar benzemez, gerçek
inceleme oranı biraz daha yüksek olabilir: `moderation-metrics` ile izlenir.

```bash
python scripts/calibrate_vision.py --models models/ --references models/references.npz \
  --seed ../../../seed --negatives <genel-fotoğraflar> --out calibration-vision.json
```

### Bağlam (`calibration-context.json`)

`scripts/calibrate_context.py` servisle aynı skoru ölçer. Setler elle etiketlidir ve prototiplerle ortak cümle
taşımaz:

| Set | Ne | Kullanım |
|:---|:---|:---|
| `data/relevance_eval.jsonl` | 77 eserle ilgili anı (eser anahtarlı; kısa anı, EN/ES/RU dahil) + 55 ilgisiz (konu dışı, spam, anlamsız) | eşik seçimi |
| `data/domain_eval.jsonl` (temiz 60) | ziyaret anıları ve şikâyetler, eser anahtarsız (yalnızca tür) | eşik seçimi (ek pozitif) |
| `data/relevance_holdout.jsonl` | 20 yeni anı + 20 yeni konu dışı | **yalnızca raporlama** (genelleme) |

| Ölçüm | Kalibrasyon | Hold-out |
|:---|:---|:---|
| AUC (spam hariç) | 0,938 | **0,973** |
| İlgili anı ≥ `ApproveFrom` (fotoğrafsız otomatik yayın) | %80 (eser anahtarlı anılar %95, kısa %83, anahtarsız şikâyetler %63) | %90 |
| Konu dışı ≥ `ApproveFrom` (kaçak) | %2,6 | %5 |
| İlgili anı < `ReviewBelow` | %1,5 | %0 |

Seçim: **`ApproveFrom = 0,38`** (spam olmayan ilgisizlerin ≤ %5'i üstünde) ve **`ReviewBelow = 0,20`** (ilgili
anıların ≤ %2'si altında). Spam eşikleri kural seviyelerinden: **`SpamReviewFrom = 0,5`**, **`SpamRejectFrom = 0,9`**.
Setler küçüktür (onlarca örnek); eşikler başlangıç değeridir, `moderation-metrics` ve personel geri alma oranıyla
izlenir. Prototip veya formül değişikliği hold-out'ta da iyileşmiyorsa ezberdir.

```bash
python scripts/calibrate_context.py --models models/ --context models/context.npz --out calibration-context.json
```

### Kanıt (`calibration-evidence.json`)

`scripts/calibrate_evidence.py`:

| Set | Ne | Kullanım |
|:---|:---|:---|
| `data/damage_eval.jsonl` | 65 hasar bildirimi (6 tür + diğer) + 37 hasar olmayan (anı, şikâyet, konu dışı, test, spam) | eşik seçimi |
| `data/damage_holdout.jsonl` | 20 + 13 yeni metin | yalnızca raporlama |
| seed görselleri (300 örnek) | 4 tür kopya (640 px JPEG, %80 kırpma, düşük kalite 400 px, parlaklık + 800 px) ↔ aynı görselin indeksteki diğer görsellere benzerliği | katalog kopyası eşiği |

| Ölçüm | Kalibrasyon | Hold-out |
|:---|:---|:---|
| Hasar tarifi AUC | 0,999 | 0,988 |
| Hasar bildirimi ≥ `damageSupportsFrom` (0,40) | %97 | %95 |
| Hasar olmayan ≥ 0,40 | %3 | %23 |
| Hasar bildirimi < `damageUnderminesBelow` (0,34) | %0 | %5 |
| Tür: doğru tür ilk sırada / 0,05 pay içinde | %82 / %92 | %63 / %84 |
| Katalog kopyası ≥ `catalogCopyFrom` (0,94): küçük resim · parlaklık · düşük kalite · kırpma | %99 · %99 · %83 · %29 | — |
| Aynı eserin başka katalog fotoğrafı ≥ 0,94 (yanlış alarm) | %0,7 | — |

Seçim: `damageSupportsFrom` hasar olmayanların ≤ %5'i üstünde; `damageUnderminesBelow` = en düşük hasar bildiriminin
0,05 altı; `catalogCopyFrom` zor negatiflerin ≤ %1'i üstünde. Hold-out'taki %23 ve %5 yüzünden politika açıklamayı tek
başına güçlü/şüpheli kanıt saymaz.

```bash
python scripts/calibrate_evidence.py --models models/ --context models/context.npz \
  --references models/references.npz --seed ../../../seed --out calibration-evidence.json
```

Yeni eşikler Memories varsayılanına (`AiModerationOptions`, `AiImageModerationOptions`, `AiContextModerationOptions`),
CivilProtection varsayılanına (`EvidenceOptions`) ve compose yapılandırmasına işlenir.

## Geliştirme

```bash
python3.13 -m venv .venv && . .venv/bin/activate
pip install -r <(python -c "import tomllib; p=tomllib.load(open('pyproject.toml','rb'))['project']; print('\n'.join(p['dependencies']+p['optional-dependencies']['dev']+p['optional-dependencies']['export']))") \
  --extra-index-url https://download.pytorch.org/whl/cpu
curl -L -o ckpt https://github.com/unitaryai/detoxify/releases/download/v0.4-alpha/multilingual_debiased-0b549669.ckpt
python scripts/export_onnx.py --checkpoint ckpt --out models/
python scripts/export_vision.py --out models/ --check-images ../../../seed/bursa/images/works/bursa-ulu-camii
python scripts/build_references.py --seed ../../../seed --models models/ --out models/references.npz
python scripts/export_context.py --out models/
python scripts/build_context.py --seed ../../../seed --models models/ --out models/context.npz
MODEL_DIR=models python -m uvicorn app.main:app --port 8090
```

```bash
python3.13 -m venv .venv && . .venv/bin/activate
pip install -r <(python -c "import tomllib; p=tomllib.load(open('pyproject.toml','rb'))['project']; print('\n'.join(p['dependencies']+p['optional-dependencies']['dev']+p['optional-dependencies']['export']))") \
  --extra-index-url https://download.pytorch.org/whl/cpu
curl -L -o ckpt https://github.com/unitaryai/detoxify/releases/download/v0.4-alpha/multilingual_debiased-0b549669.ckpt
python scripts/export_onnx.py --checkpoint ckpt --out models/      # metin; SHA-256 doğrulanır
python scripts/export_vision.py --out models/ \
  --check-images ../../../seed/bursa/images/works/bursa-ulu-camii  # NSFW + DINOv2; torch eşitliği doğrulanır
python scripts/build_references.py --seed ../../../seed --models models/ --out models/references.npz  # ~2 dk
python scripts/export_context.py --out models/                     # bağlam modeli; torch eşitliği doğrulanır
python scripts/build_context.py --seed ../../../seed --models models/ --out models/context.npz          # ~1 dk
MODEL_DIR=models SEED_DIR=../../../seed pytest                     # birim + altın testler (161)
MODEL_DIR=models pytest tests/test_golden.py                       # yalnızca metin modeli
MODEL_DIR=models SEED_DIR=../../../seed pytest tests/test_golden_vision.py   # yalnızca NSFW + benzerlik
MODEL_DIR=models pytest tests/test_golden_context.py               # yalnızca bağlam modeli
MODEL_DIR=models SEED_DIR=../../../seed pytest tests/test_golden_evidence.py  # hasar tarifi + katalog kopyası
pytest                                                             # MODEL_DIR'siz: sahte modelli birim testler
ruff check . && ruff format --check .
MODEL_DIR=models python -m uvicorn app.main:app --port 8090
```

Yeni şehir `seed/`'e eklendiğinde veya katalog görselleri/metinleri değiştiğinde `build_references.py` ve
`build_context.py` yeniden çalıştırılır; çalışan servise `REFERENCES_PATH` / `CONTEXT_PATH` ile yeni dosya
bağlanabilir. İndekste olmayan eser için benzerlik ve `monumentRelevance` `null` döner (bağlam yalnızca anı türüne
göre ölçülür). Bağlam modeli veya indeksi yoksa servis çalışır, `relevance` `null` döner ve Memories bağlam kuralını
uygulamaz.

## İmaj

```bash
docker build --build-context seed=seed -t vakif-atlas/moderation src/Services/Moderation        # modeller imajda üretilir
docker build --build-arg MODEL_SOURCE=local -t vakif-atlas/moderation src/Services/Moderation   # models/ kopyalanır
```

Export aşaması metin modelini, görsel modelleri, bağlam modelini ve iki indeksi (referans + bağlam) üretir (seed ek
`seed` bağlamından; compose bunu `additional_contexts` ile verir), ardından testleri gerçek modellerle koşar. ORT kuantizasyonu ~7 GB
bellek ister; CI runner'ında sorun yoktur. Docker VM'i küçükse (OrbStack varsayılanı 8 GB, compose yığını açıkken)
modelleri host'ta üretip `deploy/compose/.env` içinde `MODERATION_MODEL_SOURCE=local` kullanın. Çalışma imajı root
olmayan kullanıcıyla (UID 1654) salt okunur kök dosya sisteminde çalışır.

## Sözleşme

`POST /v1/moderate` — Memories'in çağırdığı uç; bir anının tamamı:

```json
{
  "monumentId": "yesil-camii",
  "texts":  [{ "id": "text", "text": "…" }, { "id": "photo-0", "text": "fotoğraf açıklaması" }],
  "images": [{ "id": "ph_…", "data": "<base64 JPEG/PNG/WebP, ≤ 5 MB>" }]
}
```

`texts` 0–5 (metin ≤ 2000), `images` 0–4, en az biri dolu →

```json
{
  "model":      { "name": "detoxify-multilingual", "version": "0.4-int8", "categories": ["toxicity", "…"] },
  "vision":     { "name": "vakifatlas-vision", "version": "1.0", "nsfw": "Falconsai/…", "embedding": "facebook/dinov2-small" },
  "references": { "version": "d59d55c0ad0a", "works": 735, "images": 3853 },
  "context":    { "name": "vakifatlas-context", "version": "1.0", "embedding": "sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2",
                  "index": "c16d85479d4c", "works": 735, "prototypes": 51 },
  "texts":  [{ "id": "text", "scores": { "toxicity": 0.0004, "…": 0 }, "lexicon": [], "language": "tr", "languageSupported": true,
               "spam": 0.0, "spamSignals": [], "relevance": 0.6992, "monumentRelevance": 0.4745 }],
  "images": [{ "id": "ph_…", "readable": true, "nsfw": 0.0001, "similarity": 0.3052, "references": 39 }]
}
```

`similarity`: eserin referansı yoksa `null`. `relevance`: bağlam modeli yoksa `null`; `monumentRelevance`: eser
bağlam indeksinde yoksa `null`. `spam`: 0 / 0,60 / 0,97. Görsel modelleri yüklü değilse fotoğraflı istek `503` alır.
Yeni alanlar eklemelidir: eski Memories sürümü onları yok sayar, yeni Memories eski servisin yanıtında bağlamı
`null`, spam'i 0 kabul eder. `images[].catalogCopy`: indeksteki tek bir görsele en yüksek kosinüs.

`POST /v1/assess-report` — CivilProtection'ın arka plan işçisi çağırır; istek `/v1/moderate` ile aynı biçimdedir
(`monumentId` ve en az bir metin zorunlu; ilk metin bildirim açıklaması, diğerleri fotoğraf betimleri). Yanıt
`/v1/moderate`'inkine ek olarak her metinde `damage {relevance, categories {structuralDamage, collapseRisk,
vandalism, theft, fireOrWater, unauthorizedWork}}` taşır (bağlam modeli yoksa `null`).

`POST /v1/classify {items: [{id, text}], monumentId?}` (1–5 öğe, metin ≤ 2000) yalnızca metin skorlar →
`{model {name, version}, context, results: [{id, scores, lexicon [{category, severity}], language, languageSupported,
spam, spamSignals, relevance, monumentRelevance}]}`.
`GET /v1/model` (metin modeli + `vision` + `references` + `context`), `GET /health/live`, `GET /health/ready`. Gateway rotası
yoktur; yalnızca `memories` ve `civil-protection` çağırır. Metin ve görsel hiçbir zaman loglanmaz.
