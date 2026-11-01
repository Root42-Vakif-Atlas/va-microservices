using System.Globalization;
using VakifAtlas.SharedKernel;

namespace VakifAtlas.Memories.Domain;

public static class MemoryStatus
{
    public const string Approved = "approved";
    public const string Pending = "pending";
    public const string Rejected = "rejected";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Approved, Pending, Rejected };
}

/// <summary>Moderasyon kararı: <c>approve</c> yayınlar/bildirimleri reddeder, <c>reject</c> yayından kaldırır.</summary>
public static class ModerationDecision
{
    public const string Approve = "approve";
    public const string Reject = "reject";
}

public static class ReportResolution
{
    public const string Open = "open";

    /// <summary>Bildirim haklı bulundu (anı reddedildi).</summary>
    public const string Upheld = "upheld";

    /// <summary>Bildirim yersiz bulundu (anı yayında kaldı).</summary>
    public const string Dismissed = "dismissed";
}

/// <summary>Anıya iliştirilmiş, yeniden kodlanmış fotoğraf (nesne anahtarları; URL yanıtta üretilir).</summary>
public sealed record MemoryPhoto(string Id, string Key, string ThumbnailKey, int Width, int Height, string? Description);

/// <summary>
/// Ziyaretçi anısı (ADR 34). Kullanıcı içeriğidir, çevrilmez. <see cref="MemoryDate"/> zaman
/// çizelgesi günüdür: ziyaret günü, yoksa paylaşım günü (Türkiye saati).
/// </summary>
public sealed class Memory
{
    public required string Id { get; init; }

    public required string MonumentId { get; init; }

    public required string AuthorId { get; init; }

    /// <summary>Herkese açık ad ("Ayşe Y."); tam ad ve e-posta saklanmaz.</summary>
    public required string AuthorName { get; init; }

    public required string Text { get; init; }

    public DateOnly? VisitedOn { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateOnly MemoryDate { get; init; }

    public string Status { get; set; } = MemoryStatus.Approved;

    public List<MemoryPhoto> Photos { get; init; } = [];

    /// <summary>
    /// Açık (karara bağlanmamış) bildirim sayısı; <see cref="MemoryRules.HideAfterReports"/> olunca
    /// VGM incelemesine kadar herkese gizlenir. Personel bildirimleri reddedince sıfırlanır.
    /// </summary>
    public int ReportCount { get; set; }

    /// <summary><c>MemoryPublished</c> olayının gönderildiği an (onay bir kez ödüllendirilir).</summary>
    public DateTimeOffset? PublishedAt { get; set; }

    public string? ModerationReason { get; private set; }

    /// <summary>Personelin iç notu (yazara gösterilmez).</summary>
    public string? ModerationNote { get; private set; }

    public string? ModeratedBy { get; private set; }

    public string? ModeratedByName { get; private set; }

    public DateTimeOffset? ModeratedAt { get; private set; }

    /// <summary>YZ önerisi (<see cref="Domain.AiVerdict"/>); <c>null</c>: YZ moderasyonu kapalıyken paylaşıldı.</summary>
    public string? AiVerdict { get; private set; }

    /// <summary>Zararlılık skoru (0–1): metin kategorilerinin ve fotoğraf NSFW olasılığının en yükseği.</summary>
    public double? AiScore { get; private set; }

    public Dictionary<string, double>? AiScores { get; private set; }

    public string? AiTopCategory { get; private set; }

    public string? AiReviewCause { get; private set; }

    /// <summary>Türkçe sözlük katmanı skoru yükseltti (moderatöre gösterilir).</summary>
    public bool AiLexiconMatched { get; private set; }

    public string? AiModelVersion { get; private set; }

    /// <summary>Fotoğrafların en yüksek NSFW olasılığı; fotoğraf skorlanmadıysa <c>null</c>.</summary>
    public double? AiNsfw { get; private set; }

    /// <summary>Fotoğrafların esere en düşük benzerliği (kosinüs, olasılık değil); eserin yeterli referansı yoksa <c>null</c>.</summary>
    public double? AiSimilarity { get; private set; }

    /// <summary>Fotoğraf başına görsel skorlar (moderatör hangi fotoğrafın işaretlendiğini görür).</summary>
    public List<AiImageScore>? AiImages { get; private set; }

    /// <summary>Görsel modeller + referans indeksi sürümü (ör. <c>1.0+d59d55c0ad0a</c>).</summary>
    public string? AiVisionVersion { get; private set; }

    /// <summary>Spam kural skoru (0 / 0,60 / 0,97); metinlerin en yükseği.</summary>
    public double? AiSpam { get; private set; }

    /// <summary>Eşleşen spam kuralları (<c>contact</c>, <c>promotion</c>, <c>scam</c>); yoksa <c>null</c>.</summary>
    public List<string>? AiSpamSignals { get; private set; }

    /// <summary>Metnin eserle bağlamı (kosinüs, olasılık değil); bağlam modeli yoksa <c>null</c>.</summary>
    public double? AiRelevance { get; private set; }

    /// <summary>Bağlam modeli + bağlam indeksi sürümü (ör. <c>1.0+c16d85479d4c</c>).</summary>
    public string? AiContextVersion { get; private set; }

    public DateTimeOffset? AiScoredAt { get; private set; }

    public int? AiLatencyMs { get; private set; }

    /// <summary>YZ servisi yanıt vermedi: anı bekler, arka plan işçisi yeniden dener (VGM kuyruğunda da görünür).</summary>
    public void MarkUnscored()
    {
        Status = MemoryStatus.Pending;
        AiVerdict = Domain.AiVerdict.Unscored;
        AiReviewCause = Domain.AiReviewCause.Unscored;
    }

    /// <summary>
    /// YZ değerlendirmesini uygular (yalnızca bekleyen anıya): onay/kaldırma <see cref="Decide"/> ile
    /// <see cref="ModerationPolicy.ActorId"/> adına verilir, inceleme anıyı bekletir. İlk yayınsa <c>true</c>.
    /// </summary>
    public bool ApplyAi(AiAssessment assessment, int latencyMs, DateTimeOffset now)
    {
        if (Status != MemoryStatus.Pending)
        {
            throw new InvalidOperationException($"AI verdict can only be applied to pending memories (status {Status}).");
        }

        AiVerdict = assessment.Verdict;
        AiScore = assessment.Score;
        AiScores = new Dictionary<string, double>(assessment.Scores, StringComparer.Ordinal);
        AiTopCategory = assessment.TopCategory;
        AiReviewCause = assessment.ReviewCause;
        AiLexiconMatched = assessment.LexiconMatched;
        AiModelVersion = assessment.ModelVersion;
        AiNsfw = assessment.Nsfw;
        AiSimilarity = assessment.Similarity;
        AiImages = assessment.Images is { Count: > 0 } images ? [.. images] : null;
        AiVisionVersion = assessment.VisionVersion;
        AiSpam = assessment.Spam;
        AiSpamSignals = assessment.SpamSignals is { Count: > 0 } signals ? [.. signals] : null;
        AiRelevance = assessment.Relevance;
        AiContextVersion = assessment.ContextVersion;
        AiScoredAt = now;
        AiLatencyMs = latencyMs;
        return assessment.Verdict switch
        {
            Domain.AiVerdict.Approve => Decide(ModerationDecision.Approve, null, null, ModerationPolicy.ActorId,
                ModerationPolicy.ActorName, now),
            Domain.AiVerdict.Reject => Decide(ModerationDecision.Reject, ModerationPolicy.RejectReason(assessment.TopCategory), null,
                ModerationPolicy.ActorId, ModerationPolicy.ActorName, now),
            _ => false,
        };
    }

    /// <summary>Son kararı YZ mi verdi (personel kararı bunu ezer).</summary>
    public bool DecidedByAi => ModeratedBy == ModerationPolicy.ActorId;

    /// <summary>Durumdan verilebilecek kararlar (bekleyen: ikisi; yayında: kaldır, açık bildirim varsa yayında tut; reddedilen: geri yükle).</summary>
    public IReadOnlyList<string> AllowedDecisions() => Status switch
    {
        MemoryStatus.Pending => [ModerationDecision.Approve, ModerationDecision.Reject],
        MemoryStatus.Approved when ReportCount > 0 => [ModerationDecision.Approve, ModerationDecision.Reject],
        MemoryStatus.Approved => [ModerationDecision.Reject],
        _ => [ModerationDecision.Approve],
    };

    /// <summary>Kararı uygular; ilk kez yayınlandıysa <c>true</c> (yayın olayı gönderilmeli).</summary>
    public bool Decide(string decision, string? reason, string? note, string actorId, string actorName, DateTimeOffset now)
    {
        if (!AllowedDecisions().Contains(decision))
        {
            throw new InvalidOperationException($"Decision {decision} is not allowed for status {Status}.");
        }

        Status = decision == ModerationDecision.Approve ? MemoryStatus.Approved : MemoryStatus.Rejected;
        ModerationReason = reason;
        ModerationNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        ModeratedBy = actorId;
        ModeratedByName = actorName;
        ModeratedAt = now;
        ReportCount = 0;
        if (Status == MemoryStatus.Approved && PublishedAt is null)
        {
            PublishedAt = now;
            return true;
        }

        return false;
    }
    public static DateOnly MemoryDateOf(DateOnly? visitedOn, DateTimeOffset createdAt) =>
        visitedOn ?? TurkeyTime.DateOf(createdAt);
}

public sealed class MemoryReport
{
    public required string MemoryId { get; init; }

    public required string ReporterId { get; init; }

    public required string Reason { get; init; }

    public string? Note { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public string Resolution { get; set; } = ReportResolution.Open;

    public DateTimeOffset? ResolvedAt { get; set; }
}

public static class MemoryRules
{
    public const int MaxTextLength = 1000;
    public const int MaxReportNoteLength = 500;
    public const int MaxModerationNoteLength = 1000;
    public const int HideAfterReports = 3;
    public const int MaxPerHour = 10;

    /// <summary>Kullanıcı başına saatte en çok bildirim; toplu gizletme (3 bildirim) kötüye kullanımını yavaşlatır.</summary>
    public const int MaxReportsPerHour = 20;

    public static readonly DateOnly EarliestVisit = new(1900, 1, 1);

    public static readonly IReadOnlySet<string> ReportReasons = new HashSet<string>(StringComparer.Ordinal)
    {
        "offensive", "inappropriate", "spam", "personalInfo", "unrelated", "other",
    };

    /// <summary>Metin kırpılmış 1–1000 karakter (Unicode kod noktası).</summary>
    public static bool IsValidText(string? text) =>
        text is not null && text.Trim() is { Length: > 0 } trimmed && trimmed.EnumerateRunes().Count() <= MaxTextLength;

    /// <summary>Ziyaret günü 1900'den önce veya gelecekte olamaz (Türkiye takvimi).</summary>
    public static bool IsValidVisit(DateOnly? visitedOn, DateOnly today) =>
        visitedOn is null || (visitedOn >= EarliestVisit && visitedOn <= today);

    public static bool IsValidReport(string? reason, string? note) =>
        reason is not null && ReportReasons.Contains(reason) && (note?.EnumerateRunes().Count() ?? 0) <= MaxReportNoteLength;

    /// <summary>Ad + soyadın baş harfi: "Ayşe Yılmaz" → "Ayşe Y." (tek kelimede olduğu gibi).</summary>
    public static string PublicDisplayName(string fullName)
    {
        var words = fullName.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2)
        {
            return words.Length == 0 ? string.Empty : words[0];
        }

        var initial = char.ConvertFromUtf32(words[^1].EnumerateRunes().First().Value);
        var upper = initial.Replace('i', 'İ').Replace('ı', 'I').ToUpper(CultureInfo.InvariantCulture);
        return $"{words[0]} {upper}.";
    }
}
