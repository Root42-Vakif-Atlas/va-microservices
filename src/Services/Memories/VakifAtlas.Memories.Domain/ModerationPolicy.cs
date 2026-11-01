namespace VakifAtlas.Memories.Domain;

/// <summary>YZ moderasyonunun önerisi (ADR 58). <c>unscored</c>: servis yanıt vermedi, yeniden denenecek.</summary>
public static class AiVerdict
{
    public const string Approve = "approve";
    public const string Review = "review";
    public const string Reject = "reject";
    public const string Unscored = "unscored";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Approve, Review, Reject, Unscored };
}

/// <summary>
/// İncelemeye düşme nedeni: ara metin skoru, NSFW şüphesi, iletişim bilgisi (olası spam), esere benzemeyen fotoğraf,
/// eserle bağlamı zayıf metin, desteklenmeyen dil, fotoğraf kuralı (okunamayan fotoğraf veya zorunlu inceleme) ya da
/// skorlanamama.
/// </summary>
public static class AiReviewCause
{
    public const string Score = "score";
    public const string Nsfw = "nsfw";
    public const string Spam = "spam";
    public const string Unrelated = "unrelated";
    public const string Context = "context";
    public const string Language = "language";
    public const string Photos = "photos";
    public const string Unscored = "unscored";
}

/// <summary>Detoxify çok dilli modelin kategorileri (her biri bağımsız 0–1 olasılık).</summary>
public static class AiCategory
{
    public const string Toxicity = "toxicity";
    public const string SevereToxicity = "severe_toxicity";
    public const string Obscene = "obscene";
    public const string IdentityAttack = "identity_attack";
    public const string Insult = "insult";
    public const string Threat = "threat";
    public const string SexualExplicit = "sexual_explicit";

    /// <summary>Fotoğraf müstehcen (NSFW sınıflandırıcısı).</summary>
    public const string Nsfw = "nsfw";

    /// <summary>Fotoğraf veya metin anının eseriyle ilgisiz (DINOv2 benzerliği / metin bağlamı eşiğin altında).</summary>
    public const string Unrelated = "unrelated";

    /// <summary>Reklam, iletişim bilgisi veya dolandırıcılık kalıbı (spam kuralları).</summary>
    public const string Spam = "spam";

    /// <summary>Metin kategorileri (<see cref="AiAssessment.Scores"/> anahtarları).</summary>
    public static readonly IReadOnlyList<string> All = [Toxicity, SevereToxicity, Obscene, IdentityAttack, Insult, Threat, SexualExplicit];
}

/// <summary>
/// Metin için üç bant: <c>skor &lt; ApproveBelow</c> otomatik onay, <c>skor ≥ RejectFrom</c> otomatik kaldırma, arası VGM
/// incelemesi. Değerler model sürümüne bağlıdır ve kalibrasyonla seçilir (Moderation/calibration.json). Spam kuralları
/// (0 / 0,60 / 0,97): <c>≥ SpamReviewFrom</c> inceleme, <c>≥ SpamRejectFrom</c> kaldırma.
/// </summary>
public sealed record AiThresholds(double ApproveBelow, double RejectFrom, double SpamReviewFrom = 0.5, double SpamRejectFrom = 0.9)
{
    public bool IsValid => ApproveBelow > 0 && ApproveBelow < RejectFrom && RejectFrom <= 1 &&
                           SpamReviewFrom > 0 && SpamReviewFrom <= SpamRejectFrom && SpamRejectFrom <= 1;
}

/// <summary>
/// Metnin eserle bağlamı (Moderation/calibration-context.json; olasılık değil, kosinüs). Otomatik onay bağlam ister:
/// metin <c>≥ ApproveFrom</c> ya da fotoğraflar esere benziyor (benzerlik kuralından geçti) ve metin
/// <c>≥ ReviewBelow</c>. Altı incelemeye düşer; bağlamla otomatik kaldırma yoktur (kısa/kişisel anı da düşük skorlanır).
/// </summary>
public sealed record AiContextThresholds(double ApproveFrom, double ReviewBelow)
{
    public bool IsValid => ReviewBelow > -1 && ReviewBelow <= ApproveFrom && ApproveFrom <= 1;
}

/// <summary>
/// Fotoğraf eşikleri (Moderation/calibration-vision.json): NSFW olasılığı <c>≥ NsfwReviewFrom</c> inceleme,
/// <c>≥ NsfwRejectFrom</c> kaldırma; eser benzerliği <c>&lt; SimilarityReviewBelow</c> inceleme,
/// <c>&lt; SimilarityRejectBelow</c> kaldırma (varsayılan kapalı). Benzerlik kuralı yalnızca eserin en az
/// <c>SimilarityMinReferences</c> referans görseli varsa uygulanır.
/// </summary>
public sealed record AiImageThresholds(
    double NsfwReviewFrom,
    double NsfwRejectFrom,
    double SimilarityReviewBelow,
    double? SimilarityRejectBelow,
    int SimilarityMinReferences)
{
    public bool IsValid => NsfwReviewFrom > 0 && NsfwReviewFrom <= NsfwRejectFrom && NsfwRejectFrom <= 1 &&
                           SimilarityReviewBelow is > -1 and <= 1 &&
                           (SimilarityRejectBelow is null || (SimilarityRejectBelow > -1 && SimilarityRejectBelow < SimilarityReviewBelow)) &&
                           SimilarityMinReferences >= 1;
}

/// <summary>
/// Tek bir metnin (anı metni veya fotoğraf betimi) skorları. <c>Spam</c>: kural skoru, <c>SpamSignals</c>: eşleşen
/// kural türleri (contact · promotion · scam). <c>Relevance</c>: eserle bağlam; bağlam modeli yoksa <c>null</c>.
/// </summary>
public sealed record AiTextScore(
    IReadOnlyDictionary<string, double> Scores,
    bool LanguageSupported,
    bool LexiconMatched,
    double Spam = 0,
    IReadOnlyList<string>? SpamSignals = null,
    double? Relevance = null);

/// <summary>
/// Bir fotoğrafın görsel skorları. <c>Similarity</c>: anının eserinin referans görselleriyle kosinüs benzerliği
/// (olasılık değil); eserin referansı yoksa <c>null</c>. <c>Readable</c> false ise skorlar boştur.
/// </summary>
public sealed record AiImageScore(string PhotoId, bool Readable, double? Nsfw, double? Similarity, int References);

/// <summary>
/// Anının birleşik YZ değerlendirmesi. <c>Score</c>: zararlılık skoru, metin kategorilerinin (metinlerin en
/// yükseği), spam kural skorunun ve fotoğraf NSFW olasılığının en yükseği; kuyruk sıralaması ve histogram bunu kullanır.
/// <c>Similarity</c>: kurala giren fotoğrafların en düşük eser benzerliği. <c>Relevance</c>: metinlerin en yüksek
/// eser bağlamı (betimlerden biri eseri anlatıyorsa bağlam vardır; spam ve zararlılık yine her metne bakar).
/// </summary>
public sealed record AiAssessment(
    string Verdict,
    double Score,
    IReadOnlyDictionary<string, double> Scores,
    string TopCategory,
    string? ReviewCause,
    bool LexiconMatched,
    string ModelVersion,
    double? Nsfw = null,
    double? Similarity = null,
    IReadOnlyList<AiImageScore>? Images = null,
    string? VisionVersion = null,
    double? Spam = null,
    IReadOnlyList<string>? SpamSignals = null,
    double? Relevance = null,
    string? ContextVersion = null);

public static class ModerationPolicy
{
    /// <summary>YZ kararlarının aktörü (<see cref="Memory.ModeratedBy"/>); personel kimlikleriyle karışmaz.</summary>
    public const string ActorId = "system:ai-moderation";

    public const string ActorName = "YZ moderasyonu";

    /// <summary>Alt kategori bu değerin altındaysa etiket genel "toxicity" kalır.</summary>
    private const double CategoryFloor = 0.2;

    /// <summary>
    /// Önce kaldırma (zararlı metin, NSFW, spam, ilgisiz fotoğraf), sonra inceleme nedenleri bu sırayla değerlendirilir;
    /// en sonda bağlam: otomatik onay için anının eserle bağlamı gösterilmeli (<see cref="AiContextThresholds"/>).
    /// <paramref name="context"/> <c>null</c> ise (kapalı) veya servis bağlam döndürmediyse bağlam kuralı uygulanmaz.
    /// <paramref name="reviewPhotos"/> açıksa fotoğraflı anı skorlardan bağımsız incelemeye düşer.
    /// </summary>
    public static AiAssessment Evaluate(IReadOnlyList<AiTextScore> texts, IReadOnlyList<AiImageScore> images, AiThresholds thresholds,
        AiImageThresholds imageThresholds, AiContextThresholds? context, bool hasPhotos, bool reviewPhotos, string modelVersion,
        string? visionVersion, string? contextVersion = null)
    {
        ArgumentOutOfRangeException.ThrowIfZero(texts.Count + images.Count);
        var scores = AiCategory.All.ToDictionary(c => c, c => texts.Count == 0 ? 0 : texts.Max(t => t.Scores.GetValueOrDefault(c)),
            StringComparer.Ordinal);
        var textScore = scores.Values.Max();
        var top = AiCategory.All.Skip(1).MaxBy(c => scores[c])!;
        var textCategory = scores[top] >= CategoryFloor ? top : AiCategory.Toxicity;

        var readable = images.Where(i => i.Readable).ToList();
        double? nsfw = readable.Count == 0 ? null : readable.Max(i => i.Nsfw ?? 0);
        var compared = readable.Where(i => i.Similarity is not null && i.References >= imageThresholds.SimilarityMinReferences).ToList();
        double? similarity = compared.Count == 0 ? null : compared.Min(i => i.Similarity!.Value);

        var spam = texts.Count == 0 ? 0 : texts.Max(t => t.Spam);
        var spamSignals = texts.SelectMany(t => t.SpamSignals ?? []).Distinct(StringComparer.Ordinal).ToArray();
        var related = texts.Where(t => t.Relevance is not null).ToList();
        double? relevance = related.Count == 0 ? null : related.Max(t => t.Relevance!.Value);
        // Fotoğraf bağlamı: en az bir fotoğraf eserin referanslarıyla karşılaştırıldı ve hepsi benzerlik kuralından geçti.
        var photosShowMonument = similarity >= imageThresholds.SimilarityReviewBelow;

        var (verdict, cause, category) = (textScore, nsfw, similarity) switch
        {
            _ when textScore >= thresholds.RejectFrom => (AiVerdict.Reject, (string?)null, textCategory),
            _ when nsfw >= imageThresholds.NsfwRejectFrom => (AiVerdict.Reject, null, AiCategory.Nsfw),
            _ when spam >= thresholds.SpamRejectFrom => (AiVerdict.Reject, null, AiCategory.Spam),
            _ when similarity < imageThresholds.SimilarityRejectBelow => (AiVerdict.Reject, null, AiCategory.Unrelated),
            _ when textScore >= thresholds.ApproveBelow => (AiVerdict.Review, AiReviewCause.Score, textCategory),
            _ when nsfw >= imageThresholds.NsfwReviewFrom => (AiVerdict.Review, AiReviewCause.Nsfw, AiCategory.Nsfw),
            _ when spam >= thresholds.SpamReviewFrom => (AiVerdict.Review, AiReviewCause.Spam, AiCategory.Spam),
            _ when similarity < imageThresholds.SimilarityReviewBelow => (AiVerdict.Review, AiReviewCause.Unrelated, AiCategory.Unrelated),
            _ when texts.Any(t => !t.LanguageSupported) => (AiVerdict.Review, AiReviewCause.Language, textCategory),
            _ when readable.Count < images.Count => (AiVerdict.Review, AiReviewCause.Photos, textCategory),
            _ when hasPhotos && reviewPhotos => (AiVerdict.Review, AiReviewCause.Photos, textCategory),
            _ when context is not null && relevance < context.ReviewBelow =>
                (AiVerdict.Review, AiReviewCause.Context, AiCategory.Unrelated),
            _ when context is not null && relevance < context.ApproveFrom && !photosShowMonument =>
                (AiVerdict.Review, AiReviewCause.Context, AiCategory.Unrelated),
            _ => (AiVerdict.Approve, null, textCategory),
        };
        return new AiAssessment(verdict, Math.Round(Math.Max(Math.Max(textScore, nsfw ?? 0), spam), 4), scores, category, cause,
            texts.Any(t => t.LexiconMatched), modelVersion, nsfw, similarity, images, visionVersion, spam,
            spamSignals.Length == 0 ? null : spamSignals, relevance, contextVersion);
    }

    /// <summary>Otomatik kaldırmanın gerekçesi (<see cref="MemoryRules.ReportReasons"/> ile aynı küme).</summary>
    public static string RejectReason(string topCategory) => topCategory switch
    {
        AiCategory.Obscene or AiCategory.SexualExplicit or AiCategory.Nsfw => "inappropriate",
        AiCategory.Unrelated => "unrelated",
        AiCategory.Spam => "spam",
        _ => "offensive",
    };
}
