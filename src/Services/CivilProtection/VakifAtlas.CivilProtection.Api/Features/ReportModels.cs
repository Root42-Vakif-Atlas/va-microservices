using System.Text.Json.Serialization;
using VakifAtlas.CatalogClient;
using VakifAtlas.CivilProtection.Domain;
using VakifAtlas.Media;

namespace VakifAtlas.CivilProtection.Api.Features;

public sealed record LocationRequest(double? Latitude, double? Longitude, double? AccuracyMeters);

public sealed record SubmitReportRequest(
    string? MonumentId, string? Category, string? Urgency, string? Description, List<PhotoAttachment>? Photos,
    LocationRequest? Location);

/// <summary><c>note</c> vatandaşa gösterilir; <c>internalNote</c> yalnızca VGM panelinde.</summary>
public sealed record StatusUpdateRequest(string? Status, string? Note, string? InternalNote);

public sealed record ReportMonument(string Id, string Name, string City, string District);

public sealed record ReportPhotoResponse(string Id, string Url, string ThumbnailUrl, int Width, int Height, string? Description);

public sealed record LocationCheckResponse(
    string Verification,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? DistanceMeters);

public sealed record HistoryResponse(string Status, DateTimeOffset At, string? Note);

public sealed record ReportResponse(
    string Id, string ReferenceCode, ReportMonument Monument, string Category, string Urgency, string Description,
    IReadOnlyList<ReportPhotoResponse> Photos, LocationCheckResponse LocationCheck, string Status,
    IReadOnlyList<HistoryResponse> History, DateTimeOffset CreatedAt)
{
    /// <summary>Özel kova: fotoğraflar kısa ömürlü (15 dk) ön imzalı adreslerle sunulur.</summary>
    public static ReportResponse From(DamageReport report, CatalogMonument? monument, IObjectStorage storage, StorageOptions options)
    {
        var lifetime = TimeSpan.FromMinutes(15);
        return new ReportResponse(
            report.Id, report.ReferenceCode,
            new ReportMonument(report.MonumentId, monument?.Name ?? report.MonumentId, monument?.City ?? string.Empty,
                monument?.District ?? string.Empty),
            report.Category, report.Urgency, report.Description,
            report.Photos.Select(p => new ReportPhotoResponse(p.Id, storage.PresignGet(options.Buckets.Private, p.Key, lifetime),
                storage.PresignGet(options.Buckets.Private, p.ThumbnailKey, lifetime), p.Width, p.Height, p.Description)).ToArray(),
            new LocationCheckResponse(report.LocationCheck.Verification, report.LocationCheck.DistanceMeters),
            report.Status, report.History.Select(h => new HistoryResponse(h.Status, h.At, h.Note)).ToArray(), report.CreatedAt);
    }
}

public sealed record StaffActor(string Id, string? DisplayName);

public sealed record EvidenceReasonResponse(
    string Code,
    string Effect,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Value,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Detail);

/// <summary>
/// Kanıt etiketi (yalnızca VGM paneli): <c>strong</c> · <c>unverified</c> · <c>suspicious</c> ve gerekçeleri
/// (<c>supports</c> · <c>undermines</c> · <c>neutral</c>). Moderatöre yardımcıdır; durumu değiştirmez.
/// </summary>
public sealed record EvidenceResponse(string Level, IReadOnlyList<EvidenceReasonResponse> Reasons, string? Version,
    DateTimeOffset? ScoredAt)
{
    public static EvidenceResponse? From(DamageReport report) => report.EvidenceLevel is null
        ? null
        : new EvidenceResponse(report.EvidenceLevel,
            (report.EvidenceReasons ?? []).Select(r => new EvidenceReasonResponse(r.Code, r.Effect,
                r.Value is { } value ? Math.Round(value, 4) : null, r.Detail)).ToArray(),
            report.EvidenceVersion, report.EvidenceScoredAt);
}

public sealed record StaffHistoryResponse(string Status, DateTimeOffset At, string? Note, string? InternalNote, StaffActor? Actor);

/// <summary>
/// VGM paneli görünümü: vatandaş görünümüne ek olarak ham konum, personel geçmişi (iç not, işlemi
/// yapan), panelde sunulacak sonraki durumlar, hesap silindiyse <c>anonymized</c> ve kanıt etiketi
/// (<c>evidence</c>; <c>null</c>: henüz değerlendirilmedi veya değerlendirme kapalı).
/// </summary>
public sealed record StaffReportResponse(
    string Id, string ReferenceCode, ReportMonument Monument, string Category, string Urgency, string Description,
    IReadOnlyList<ReportPhotoResponse> Photos, LocationCheckResponse LocationCheck, LocationRequest? Location, string Status,
    IReadOnlyList<string> AllowedTransitions, IReadOnlyList<StaffHistoryResponse> History, bool Anonymized,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, EvidenceResponse? Evidence)
{
    public static StaffReportResponse From(DamageReport report, CatalogMonument? monument, ReportLocation? location,
        IObjectStorage storage, StorageOptions options)
    {
        var citizen = ReportResponse.From(report, monument, storage, options);
        return new StaffReportResponse(
            report.Id, report.ReferenceCode, citizen.Monument, report.Category, report.Urgency, report.Description,
            citizen.Photos, citizen.LocationCheck,
            location is null ? null : new LocationRequest(location.Latitude, location.Longitude, location.AccuracyMeters),
            report.Status, ReportStatus.Next(report.Status),
            report.History.Select(h => new StaffHistoryResponse(h.Status, h.At, h.Note, h.InternalNote,
                h.ActorId is null ? null : new StaffActor(h.ActorId, h.ActorName))).ToArray(),
            report.OwnerId is null, report.CreatedAt, report.UpdatedAt, EvidenceResponse.From(report));
    }

    /// <summary>Durumu ve geçmişi değiştiren her güncellemede değişir (fotoğraf adresleri hariç).</summary>
    public static string ETagOf(DamageReport report) => VakifAtlas.ServiceDefaults.Admin.Preconditions.ETagOf(
        report.Status, report.History.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
        report.UpdatedAt.ToString("O"), report.OwnerId is null ? "anon" : "owned");
}

/// <summary><c>OpenByEvidence</c>: açık bildirimlerin kanıt etiketleri (<c>pending</c>: henüz değerlendirilmedi).</summary>
public sealed record StaffReportSummary(
    int Total,
    int Open,
    IReadOnlyDictionary<string, int> ByStatus,
    IReadOnlyDictionary<string, int> OpenByUrgency,
    IReadOnlyDictionary<string, int> OpenByEvidence);
