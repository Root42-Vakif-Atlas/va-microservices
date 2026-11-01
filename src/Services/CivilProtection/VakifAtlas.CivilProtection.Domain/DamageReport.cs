using VakifAtlas.SharedKernel;

namespace VakifAtlas.CivilProtection.Domain;

public static class ReportStatus
{
    public const string Received = "received";
    public const string InReview = "inReview";
    public const string Forwarded = "forwarded";
    public const string Resolved = "resolved";
    public const string Closed = "closed";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Received, InReview, Forwarded, Resolved, Closed,
    };

    /// <summary>
    /// alındı → inceleniyor → yetkili birime iletildi → sonuçlandı; işlem gerektirmeyen rapor
    /// sonuçlanmadan her aşamada kapatılabilir. Sonuç durumlarından çıkış yoktur.
    /// </summary>
    public static bool CanMove(string from, string to) => (from, to) switch
    {
        (Received, InReview) => true,
        (InReview, Forwarded) => true,
        (Forwarded, Resolved) => true,
        (InReview, Resolved) => true,
        (Received or InReview or Forwarded, Closed) => true,
        _ => false,
    };

    private static readonly string[] Order = [Received, InReview, Forwarded, Resolved, Closed];

    /// <summary>Durumdan gidilebilecek sonraki durumlar (panelde yalnızca bunlar sunulur).</summary>
    public static IReadOnlyList<string> Next(string from) => Order.Where(to => CanMove(from, to)).ToArray();

    public static bool IsOpen(string status) => status is Received or InReview or Forwarded;
}

public static class LocationVerification
{
    public const string NotProvided = "notProvided";
    public const string OnSite = "onSite";
    public const string Distant = "distant";
}

public sealed record ReportPhoto(string Id, string Key, string ThumbnailKey, int Width, int Height, string? Description);

/// <summary>
/// Durum geçişi. <see cref="Note"/> vatandaşa gösterilir; <see cref="InternalNote"/> ve işlemi
/// yapan personel (<see cref="ActorId"/>, <see cref="ActorName"/>) yalnızca VGM panelinde görünür.
/// </summary>
public sealed record StatusChange(string Status, DateTimeOffset At, string? Note, string? InternalNote = null,
    string? ActorId = null, string? ActorName = null);

/// <summary>Vatandaşa dönen konum doğrulaması; ham koordinat yanıtta yer almaz.</summary>
public sealed record LocationCheck(string Verification, int? DistanceMeters)
{
    public const int OnSiteRadiusMeters = 500;
    public const int MaxAccuracyMeters = 200;

    public static readonly LocationCheck NotProvided = new(LocationVerification.NotProvided, null);

    /// <summary>
    /// Esere ≤ 500 m ve konum doğruluğu ≤ 200 m → yerinde. Eserin konumu katalogda henüz yoksa
    /// yerinde olduğu doğrulanamaz: uzak, mesafesiz.
    /// </summary>
    public static LocationCheck Evaluate(GeoPoint reporter, double accuracyMeters, GeoPoint? monumentLocation)
    {
        if (monumentLocation is not { } monument)
        {
            return new LocationCheck(LocationVerification.Distant, null);
        }

        var distance = (int)Math.Round(Geo.DistanceMeters(reporter, monument));
        var onSite = distance <= OnSiteRadiusMeters && accuracyMeters <= MaxAccuracyMeters;
        return new LocationCheck(onSite ? LocationVerification.OnSite : LocationVerification.Distant, distance);
    }
}

/// <summary>
/// Hasar bildirimi (ADR 35). Kültür varlığı koruma kaydıdır: hesap silinse de kalır,
/// kişiyle bağı (<see cref="OwnerId"/>) kaldırılır.
/// </summary>
public sealed class DamageReport
{
    public required string Id { get; init; }

    /// <summary><c>SK-&lt;yıl&gt;-&lt;6 hane&gt;</c>; vatandaşın VGM ile iletişimde kullandığı numara.</summary>
    public required string ReferenceCode { get; init; }

    public string? OwnerId { get; set; }

    public required string MonumentId { get; init; }

    public required string Category { get; init; }

    public required string Urgency { get; init; }

    public required string Description { get; init; }

    public List<ReportPhoto> Photos { get; init; } = [];

    public required LocationCheck LocationCheck { get; init; }

    public string Status { get; private set; } = ReportStatus.Received;

    public List<StatusChange> History { get; init; } = [];

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Kanıt etiketi (<see cref="Domain.EvidenceLevel"/>); <c>null</c>: henüz skorlanmadı. Durumu değiştirmez.</summary>
    public string? EvidenceLevel { get; private set; }

    public List<EvidenceReason>? EvidenceReasons { get; private set; }

    /// <summary>Moderasyon servisinin ham sinyalleri (başka bildirim gelince etiket bunlardan yeniden hesaplanır).</summary>
    public EvidenceSignals? EvidenceSignals { get; private set; }

    /// <summary>Model + indeks sürümleri (ör. <c>0.4-int8/1.0+d59d55c0ad0a/1.0+d8e65b479782</c>).</summary>
    public string? EvidenceVersion { get; private set; }

    public DateTimeOffset? EvidenceScoredAt { get; private set; }

    public static DamageReport Submit(string id, string referenceCode, string ownerId, string monumentId, string category,
        string urgency, string description, IEnumerable<ReportPhoto> photos, LocationCheck locationCheck, DateTimeOffset now)
    {
        var report = new DamageReport
        {
            Id = id,
            ReferenceCode = referenceCode,
            OwnerId = ownerId,
            MonumentId = monumentId,
            Category = category,
            Urgency = urgency,
            Description = description,
            Photos = [.. photos],
            LocationCheck = locationCheck,
            CreatedAt = now,
        };
        report.History.Add(new StatusChange(ReportStatus.Received, now, null));
        report.UpdatedAt = now;
        return report;
    }

    /// <summary>VGM'nin durum güncellemesi (isteğe bağlı vatandaşa not ve personel iç notu).</summary>
    public bool MoveTo(string status, string? note, DateTimeOffset now, string? internalNote = null,
        string? actorId = null, string? actorName = null)
    {
        if (!ReportStatus.CanMove(Status, status))
        {
            return false;
        }

        Status = status;
        UpdatedAt = now;
        History.Add(new StatusChange(status, now, Clean(note), Clean(internalNote), actorId, actorName));
        return true;
    }

    /// <summary>Skorlama sonucunu ve etiketi yazar (durum ve <see cref="UpdatedAt"/> değişmez: vatandaş görünümü aynı kalır).</summary>
    public void ApplyEvidence(EvidenceSignals signals, EvidenceAssessment assessment, string version, DateTimeOffset now)
    {
        EvidenceSignals = signals;
        EvidenceVersion = version;
        EvidenceScoredAt = now;
        Reassess(assessment);
    }

    /// <summary>Saklı sinyallerden yeniden hesaplanan etiketi yazar (ör. aynı esere yeni bildirim); değiştiyse <c>true</c>.</summary>
    public bool Reassess(EvidenceAssessment assessment)
    {
        var changed = EvidenceLevel != assessment.Level || EvidenceReasons is null || !EvidenceReasons.SequenceEqual(assessment.Reasons);
        EvidenceLevel = assessment.Level;
        EvidenceReasons = [.. assessment.Reasons];
        return changed;
    }

    /// <summary>Personel bu raporda işlem yaptı mı (demo ilerlemesi personelin kararını ezmez).</summary>
    public bool HasStaffAction => History.Any(h => h.ActorId is not null);

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    public bool IsFinal => Status is ReportStatus.Resolved or ReportStatus.Closed;
}

/// <summary>
/// Ham konum (KVKK açık rıza, yalnızca bu rapor). VGM incelemesi içindir; rapor kapandıktan sonra
/// saklama süresi dolunca silinir.
/// </summary>
public sealed class ReportLocation
{
    public required string ReportId { get; init; }

    public double Latitude { get; init; }

    public double Longitude { get; init; }

    public double AccuracyMeters { get; init; }

    public DateTimeOffset? DeleteAfter { get; set; }
}

public static class DamageReportRules
{
    public const int MinDescription = 20;
    public const int MaxDescription = 2000;
    public const int MaxPerHour = 5;
    public const int MaxNoteLength = 1000;

    public static readonly TimeSpan LocationRetention = TimeSpan.FromDays(90);

    public static readonly IReadOnlySet<string> Categories = new HashSet<string>(StringComparer.Ordinal)
    {
        "structuralDamage", "collapseRisk", "vandalism", "theft", "fireOrWater", "unauthorizedWork", "other",
    };

    public static readonly IReadOnlySet<string> Urgencies = new HashSet<string>(StringComparer.Ordinal)
    {
        "low", "medium", "high", "critical",
    };

    public static bool IsValidDescription(string? description) =>
        description?.Trim().EnumerateRunes().Count() is >= MinDescription and <= MaxDescription;

    public static bool IsValidLocation(double latitude, double longitude, double accuracy) =>
        new GeoPoint(latitude, longitude).IsValid && double.IsFinite(accuracy) && accuracy >= 0;

    public static string ReferenceCode(int year, long sequence) =>
        $"SK-{year}-{sequence.ToString("D6", System.Globalization.CultureInfo.InvariantCulture)}";
}
