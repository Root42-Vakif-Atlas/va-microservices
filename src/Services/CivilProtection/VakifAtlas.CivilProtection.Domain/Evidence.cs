namespace VakifAtlas.CivilProtection.Domain;

/// <summary>
/// Bildirimin kanıt düzeyi (moderatöre yardımcı etiket; durumu değiştirmez, vatandaşa gösterilmez). YZ bir bildirimin
/// doğru ya da yanlış olduğunu bilemez, yalnızca kanıtın gücünü ölçer: "şüpheli" somut olumsuz işaret ister, kanıt
/// yokluğu "doğrulanamadı"dır.
/// </summary>
public static class EvidenceLevel
{
    public const string Strong = "strong";
    public const string Unverified = "unverified";
    public const string Suspicious = "suspicious";

    /// <summary>Henüz skorlanmadı (panel süzgeci; alan <c>null</c>).</summary>
    public const string Pending = "pending";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Strong, Unverified, Suspicious };

    /// <summary>Kuyruk sırası: aynı aciliyette önce güçlü kanıt, şüpheli en sonda.</summary>
    public static int Rank(string? level) => level switch
    {
        Strong => 0,
        Unverified => 1,
        Suspicious => 3,
        _ => 2,
    };
}

/// <summary>Gerekçenin etkisi: destekler, zayıflatır veya yalnızca bilgi.</summary>
public static class EvidenceEffect
{
    public const string Supports = "supports";
    public const string Undermines = "undermines";
    public const string Neutral = "neutral";
}

/// <summary>Gerekçe kodları; panel bunları yerelleştirir (değer ve ayrıntı koda göre anlam taşır).</summary>
public static class EvidenceCode
{
    /// <summary>Konum esere ≤ 500 m ve doğruluk ≤ 200 m (değer: mesafe, m).</summary>
    public const string LocationOnSite = "location.onSite";

    public const string LocationDistant = "location.distant";
    public const string LocationNotProvided = "location.notProvided";

    /// <summary>Aynı esere yakın zamanda başka hesaplardan bildirim (değer: hesap sayısı).</summary>
    public const string Corroborated = "corroborated";

    /// <summary>Fotoğraf eserin katalog görsellerine benziyor (değer: en yüksek benzerlik).</summary>
    public const string PhotoMatchesMonument = "photo.matchesMonument";

    /// <summary>Fotoğraf esere benzemiyor; hasar yakın çekimi de olabilir, olumsuz sayılmaz.</summary>
    public const string PhotoLowSimilarity = "photo.lowSimilarity";

    public const string PhotoNone = "photo.none";
    public const string PhotoUnreadable = "photo.unreadable";

    /// <summary>Fotoğraf bir katalog görselinin kopyası olabilir (değer: en yüksek benzerlik).</summary>
    public const string PhotoCatalogCopy = "photo.catalogCopy";

    public const string PhotoNsfw = "photo.nsfw";

    /// <summary>Açıklama hasar tarif ediyor (değer: hasar prototiplerine benzerlik).</summary>
    public const string TextDescribesDamage = "text.describesDamage";

    public const string TextWeakDamage = "text.weakDamage";

    /// <summary>Açıklama hasar tarif etmiyor (test, konu dışı, anı).</summary>
    public const string TextNotDamage = "text.notDamage";

    public const string TextAbusive = "text.abusive";

    /// <summary>Spam kuralı (ayrıntı: eşleşen kural türleri).</summary>
    public const string TextSpam = "text.spam";

    /// <summary>Açıklamada iletişim bilgisi var (kişisel veri olabilir).</summary>
    public const string TextContact = "text.contact";

    /// <summary>Açıklama seçilen türden başka bir türe daha yakın (ayrıntı: önerilen tür). Yalnızca bilgi.</summary>
    public const string CategorySuggested = "category.suggested";

    /// <summary>Yüksek/kritik aciliyet ama bağımsız kanıt yok. Yalnızca bilgi; aciliyet sırası değişmez.</summary>
    public const string UrgencyUnsupported = "urgency.unsupported";
}

public sealed record EvidenceReason(string Code, string Effect, double? Value = null, string? Detail = null);

/// <summary>Fotoğrafın görsel sinyalleri (moderasyon servisi): NSFW, esere benzerlik, katalog kopyası.</summary>
public sealed record EvidencePhoto(string PhotoId, bool Readable, double? Nsfw, double? Similarity, int References, double? CatalogCopy);

/// <summary>
/// Moderasyon servisinin ham sinyalleri (saklanır: başka bildirim geldiğinde etiket yeniden hesaplanır). Metin
/// sinyalleri açıklama + fotoğraf betimlerinin en kötüsüdür; hasar sinyalleri yalnızca açıklamadandır. Bağlam modeli
/// yoksa <c>DamageText</c>/<c>Categories</c> <c>null</c>.
/// </summary>
public sealed record EvidenceSignals(
    double Toxicity,
    double Spam,
    IReadOnlyList<string>? SpamSignals,
    double? DamageText,
    IReadOnlyDictionary<string, double>? Categories,
    IReadOnlyList<EvidencePhoto> Photos);

/// <summary>Eşikler Moderation/calibration-evidence.json (hasar metni, katalog kopyası) ve mevcut moderasyon eşiklerinden.</summary>
public sealed record EvidenceThresholds(
    double DamageSupportsFrom = 0.40,
    double DamageUnderminesBelow = 0.34,
    double CatalogCopyFrom = 0.94,
    double CategoryMargin = 0.05,
    double AbusiveFrom = 0.96,
    double SpamFrom = 0.9,
    double ContactFrom = 0.5,
    double NsfwFrom = 0.9,
    double SimilarityFrom = 0.11,
    int SimilarityMinReferences = 3)
{
    public bool IsValid => DamageUnderminesBelow <= DamageSupportsFrom && DamageSupportsFrom <= 1 && CatalogCopyFrom is > 0 and <= 1 &&
                           CategoryMargin >= 0 && AbusiveFrom is > 0 and <= 1 && ContactFrom > 0 && ContactFrom <= SpamFrom &&
                           SpamFrom <= 1 && NsfwFrom is > 0 and <= 1 && SimilarityFrom is > -1 and <= 1 && SimilarityMinReferences >= 1;
}

public sealed record EvidenceAssessment(string Level, IReadOnlyList<EvidenceReason> Reasons);

/// <summary>
/// Kanıt etiketi. Bağımsız kanıt: yerinde konum, esere benzeyen fotoğraf, başka hesaplardan bildirim (hesap başına bir,
/// en çok iki).
/// <list type="bullet">
/// <item><b>Şüpheli</b>: somut olumsuz işaret (hakaret, spam, müstehcen fotoğraf, katalog kopyası) ya da açıklama hasar
/// tarif etmiyor ve hiçbir destek yok (fotoğraf yok, yerinde değil, başka bildirim yok).</item>
/// <item><b>Güçlü</b>: olumsuz işaret yok ve en az iki bağımsız kanıt ya da bir bağımsız kanıt + hasar tarif eden açıklama.
/// Açıklama tek başına yetmez (hold-out'ta hasar olmayan metnin %23'ü hasar eşiğini geçti).</item>
/// <item>Diğerleri <b>doğrulanamadı</b>.</item>
/// </list>
/// Tür uyumu ve aciliyet yalnızca bilgidir.
/// </summary>
public static class EvidencePolicy
{
    public static EvidenceAssessment Evaluate(EvidenceSignals signals, LocationCheck location, string category, string urgency,
        int corroborations, EvidenceThresholds thresholds)
    {
        var reasons = new List<EvidenceReason>();
        var hard = false;
        var independent = 0;

        switch (location.Verification)
        {
            case LocationVerification.OnSite:
                reasons.Add(new(EvidenceCode.LocationOnSite, EvidenceEffect.Supports, location.DistanceMeters));
                independent++;
                break;
            case LocationVerification.Distant:
                reasons.Add(new(EvidenceCode.LocationDistant, EvidenceEffect.Neutral, location.DistanceMeters));
                break;
            default:
                reasons.Add(new(EvidenceCode.LocationNotProvided, EvidenceEffect.Neutral));
                break;
        }

        if (corroborations > 0)
        {
            reasons.Add(new(EvidenceCode.Corroborated, EvidenceEffect.Supports, corroborations));
            // Her hesap ayrı tanıktır (deprem sonrası çoklu bildirim); en çok iki kanıt sayılır.
            independent += Math.Min(corroborations, 2);
        }

        var readable = signals.Photos.Where(p => p.Readable).ToList();
        if (signals.Photos.Count == 0)
        {
            reasons.Add(new(EvidenceCode.PhotoNone, EvidenceEffect.Neutral));
        }

        if (readable.Where(p => p.CatalogCopy >= thresholds.CatalogCopyFrom).MaxBy(p => p.CatalogCopy) is { } copy)
        {
            reasons.Add(new(EvidenceCode.PhotoCatalogCopy, EvidenceEffect.Undermines, copy.CatalogCopy));
            hard = true;
        }

        if (readable.Where(p => p.Nsfw >= thresholds.NsfwFrom).MaxBy(p => p.Nsfw) is { } nsfw)
        {
            reasons.Add(new(EvidenceCode.PhotoNsfw, EvidenceEffect.Undermines, nsfw.Nsfw));
            hard = true;
        }

        // Kopya fotoğraf esere elbette benzer: benzerlik kanıtı yalnızca kopya olmayan fotoğraflardan.
        var compared = readable.Where(p => p.Similarity is not null && p.References >= thresholds.SimilarityMinReferences &&
                                           !(p.CatalogCopy >= thresholds.CatalogCopyFrom)).ToList();
        if (compared.Count > 0)
        {
            var best = compared.Max(p => p.Similarity!.Value);
            if (best >= thresholds.SimilarityFrom)
            {
                reasons.Add(new(EvidenceCode.PhotoMatchesMonument, EvidenceEffect.Supports, best));
                independent++;
            }
            else
            {
                reasons.Add(new(EvidenceCode.PhotoLowSimilarity, EvidenceEffect.Neutral, best));
            }
        }

        if (readable.Count < signals.Photos.Count)
        {
            reasons.Add(new(EvidenceCode.PhotoUnreadable, EvidenceEffect.Neutral, signals.Photos.Count - readable.Count));
        }

        if (signals.Toxicity >= thresholds.AbusiveFrom)
        {
            reasons.Add(new(EvidenceCode.TextAbusive, EvidenceEffect.Undermines, signals.Toxicity));
            hard = true;
        }

        var spamDetail = signals.SpamSignals is { Count: > 0 } spamSignals ? string.Join(',', spamSignals) : null;
        if (signals.Spam >= thresholds.SpamFrom)
        {
            reasons.Add(new(EvidenceCode.TextSpam, EvidenceEffect.Undermines, signals.Spam, spamDetail));
            hard = true;
        }
        else if (signals.Spam >= thresholds.ContactFrom)
        {
            reasons.Add(new(EvidenceCode.TextContact, EvidenceEffect.Neutral, signals.Spam, spamDetail));
        }

        var describesDamage = false;
        var notDamage = false;
        if (signals.DamageText is { } damage)
        {
            describesDamage = damage >= thresholds.DamageSupportsFrom;
            notDamage = damage < thresholds.DamageUnderminesBelow;
            reasons.Add(describesDamage
                ? new(EvidenceCode.TextDescribesDamage, EvidenceEffect.Supports, damage)
                : notDamage
                    ? new(EvidenceCode.TextNotDamage, EvidenceEffect.Undermines, damage)
                    : new(EvidenceCode.TextWeakDamage, EvidenceEffect.Neutral, damage));
        }

        if (signals.Categories is { Count: > 0 } categories && category != "other" && categories.ContainsKey(category))
        {
            var best = categories.MaxBy(c => c.Value);
            if (best.Key != category && categories[category] < best.Value - thresholds.CategoryMargin)
            {
                reasons.Add(new(EvidenceCode.CategorySuggested, EvidenceEffect.Neutral, best.Value, best.Key));
            }
        }

        if (urgency is "critical" or "high" && independent == 0)
        {
            reasons.Add(new(EvidenceCode.UrgencyUnsupported, EvidenceEffect.Neutral, Detail: urgency));
        }

        var level = hard || (notDamage && independent == 0 && signals.Photos.Count == 0)
            ? EvidenceLevel.Suspicious
            : independent >= 2 || (independent >= 1 && describesDamage)
                ? EvidenceLevel.Strong
                : EvidenceLevel.Unverified;
        return new EvidenceAssessment(level, reasons);
    }
}
