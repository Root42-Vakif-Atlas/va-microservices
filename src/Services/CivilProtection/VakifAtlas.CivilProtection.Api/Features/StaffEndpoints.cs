using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using VakifAtlas.CatalogClient;
using VakifAtlas.CivilProtection.Api.Infrastructure;
using VakifAtlas.CivilProtection.Domain;
using VakifAtlas.Contracts;
using VakifAtlas.Media;
using VakifAtlas.Messaging;
using VakifAtlas.ServiceDefaults.Admin;
using VakifAtlas.ServiceDefaults.Auth;
using VakifAtlas.ServiceDefaults.Http;
using VakifAtlas.SharedKernel;

namespace VakifAtlas.CivilProtection.Api.Features;

/// <summary>
/// VGM Yönetim Paneli (<c>civil-protection/admin</c>, rol <c>moderator</c> veya
/// <c>vgm_admin</c>): süzgeçli kuyruk, ayrıntı, durum geçişi (<c>If-Match</c>) ve özet. Kanıt etiketi
/// (<c>evidence=</c> süzgeci, <c>sort=evidence</c>: aciliyet, sonra kanıt) yalnızca yardımcıdır. Her geçiş
/// işlemi yapan personelle geçmişe ve denetim kaydına yazılır. Sonuçlanan/kapatılan raporun ham
/// konumu saklama süresi sonunda silinir.
/// </summary>
public static class StaffEndpoints
{
    private static readonly string[] UrgencyOrder = ["critical", "high", "medium", "low"];

    public static RouteGroupBuilder MapStaffEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("reports", Queue);
        group.MapGet("reports/{id}", Get);
        group.MapPost("reports/{id}/status", UpdateStatus);
        group.MapGet("summary", Summary);
        return group;
    }

    private static async Task<IResult> Queue(HttpContext context, CivilProtectionDbContext db, ICatalogClient catalog,
        IObjectStorage storage, IOptions<StorageOptions> options, CancellationToken cancellationToken)
    {
        var query = context.Request.Query;
        var limit = QueryParsing.Limit(query["limit"], 20, 100);
        var statuses = QueryParsing.CommaList(query["status"]);
        var urgencies = QueryParsing.CommaList(query["urgency"]);
        var categories = QueryParsing.CommaList(query["category"]);
        var evidence = QueryParsing.CommaList(query["evidence"]);
        var monumentId = query["monumentId"].ToString();
        var search = query["q"].ToString().Trim();
        var sort = query["sort"].ToString() is { Length: > 0 } value ? value : "newest";
        if (limit is null || statuses.Any(s => !ReportStatus.All.Contains(s) && s != "open") ||
            urgencies.Any(u => !DamageReportRules.Urgencies.Contains(u)) ||
            categories.Any(c => !DamageReportRules.Categories.Contains(c)) ||
            evidence.Any(e => !EvidenceLevel.All.Contains(e) && e != EvidenceLevel.Pending) || monumentId.Length > 120 ||
            search.Length > 40 || sort is not ("newest" or "oldest" or "urgency" or "evidence"))
        {
            return ApiError.BadRequest().ToHttpResult();
        }

        var reports = db.Reports.AsNoTracking();
        if (statuses.Count > 0)
        {
            var wanted = statuses.Contains("open")
                ? statuses.Where(s => s != "open").Concat([ReportStatus.Received, ReportStatus.InReview, ReportStatus.Forwarded]).ToArray()
                : statuses.ToArray();
            reports = reports.Where(r => wanted.Contains(r.Status));
        }

        if (urgencies.Count > 0)
        {
            reports = reports.Where(r => urgencies.Contains(r.Urgency));
        }

        if (categories.Count > 0)
        {
            reports = reports.Where(r => categories.Contains(r.Category));
        }

        if (evidence.Count > 0)
        {
            var levels = evidence.Where(e => e != EvidenceLevel.Pending).ToArray();
            var pending = evidence.Contains(EvidenceLevel.Pending);
            reports = reports.Where(r => (r.EvidenceLevel != null && levels.Contains(r.EvidenceLevel)) || (pending && r.EvidenceLevel == null));
        }

        if (monumentId.Length > 0)
        {
            reports = reports.Where(r => r.MonumentId == monumentId);
        }

        if (search.Length > 0)
        {
            var reference = search.ToUpperInvariant();
            reports = reports.Where(r => r.ReferenceCode.StartsWith(reference) || r.Id == search);
        }

        var total = await reports.CountAsync(cancellationToken);
        // evidence: aciliyet × 4 + kanıt sırası (güçlü 0, doğrulanamadı 1, bekliyor 2, şüpheli 3; EvidenceLevel.Rank).
        // Kanıt aciliyeti ezmez: şüpheli kritik bildirim yine yüksek aciliyetin önündedir.
        var ranked = reports.Select(r => new
        {
            Report = r,
            Rank = (r.Urgency == "critical" ? 0 : r.Urgency == "high" ? 1 : r.Urgency == "medium" ? 2 : 3) * (sort == "evidence" ? 4 : 1) +
                   (sort != "evidence" ? 0 :
                       r.EvidenceLevel == EvidenceLevel.Strong ? 0 :
                       r.EvidenceLevel == EvidenceLevel.Unverified ? 1 :
                       r.EvidenceLevel == EvidenceLevel.Suspicious ? 3 : 2),
        });
        if (query["cursor"].ToString() is { Length: > 0 } cursor)
        {
            if (CursorCodec.Decode(cursor, 3) is not [var rankText, var createdText, var id] ||
                !int.TryParse(rankText, System.Globalization.CultureInfo.InvariantCulture, out var rank) ||
                !CursorCodec.TryInstant(createdText, out var createdAt))
            {
                return ApiError.BadRequest(ErrorCodes.InvalidCursor).ToHttpResult();
            }

            ranked = sort switch
            {
                "oldest" => ranked.Where(x => x.Report.CreatedAt > createdAt ||
                                              (x.Report.CreatedAt == createdAt && string.Compare(x.Report.Id, id) > 0)),
                "urgency" or "evidence" => ranked.Where(x => x.Rank > rank || (x.Rank == rank && (x.Report.CreatedAt < createdAt ||
                    (x.Report.CreatedAt == createdAt && string.Compare(x.Report.Id, id) < 0)))),
                _ => ranked.Where(x => x.Report.CreatedAt < createdAt ||
                                       (x.Report.CreatedAt == createdAt && string.Compare(x.Report.Id, id) < 0)),
            };
        }

        ranked = sort switch
        {
            "oldest" => ranked.OrderBy(x => x.Report.CreatedAt).ThenBy(x => x.Report.Id),
            "urgency" or "evidence" => ranked.OrderBy(x => x.Rank).ThenByDescending(x => x.Report.CreatedAt)
                .ThenByDescending(x => x.Report.Id),
            _ => ranked.OrderByDescending(x => x.Report.CreatedAt).ThenByDescending(x => x.Report.Id),
        };
        var rows = await ranked.Take(limit.Value + 1).ToListAsync(cancellationToken);
        var next = rows.Count > limit
            ? CursorCodec.Encode(rows[limit.Value - 1].Rank.ToString(System.Globalization.CultureInfo.InvariantCulture),
                CursorCodec.Instant(rows[limit.Value - 1].Report.CreatedAt), rows[limit.Value - 1].Report.Id)
            : null;
        var page = rows.Take(limit.Value).Select(x => x.Report).ToList();
        var ids = page.Select(r => r.Id).ToList();
        var locations = await db.Locations.AsNoTracking().Where(l => ids.Contains(l.ReportId))
            .ToDictionaryAsync(l => l.ReportId, cancellationToken);
        var monuments = await catalog.GetAsync(page.Select(r => r.MonumentId).Distinct(), RequestLanguage.Resolve(context.Request),
            cancellationToken);
        return TypedResults.Ok(new Page<StaffReportResponse>(page.Select(r => StaffReportResponse.From(r,
            monuments.GetValueOrDefault(r.MonumentId), locations.GetValueOrDefault(r.Id), storage, options.Value)).ToArray(),
            total, next));
    }

    private static async Task<IResult> Get(string id, HttpContext context, CivilProtectionDbContext db, ICatalogClient catalog,
        IObjectStorage storage, IOptions<StorageOptions> options, CancellationToken cancellationToken)
    {
        var report = await db.Reports.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (report is null)
        {
            return ApiError.NotFound().ToHttpResult();
        }

        var location = await db.Locations.AsNoTracking().SingleOrDefaultAsync(l => l.ReportId == id, cancellationToken);
        var monuments = await catalog.GetAsync([report.MonumentId], RequestLanguage.Resolve(context.Request), cancellationToken);
        context.Response.SetETag(StaffReportResponse.ETagOf(report));
        return TypedResults.Ok(StaffReportResponse.From(report, monuments.GetValueOrDefault(report.MonumentId), location, storage,
            options.Value));
    }

    private static async Task<IResult> UpdateStatus(string id, StatusUpdateRequest request, HttpContext context,
        CivilProtectionDbContext db, ICatalogClient catalog, IObjectStorage storage, IOptions<StorageOptions> options,
        StaffAudit audit, IEventPublisher events, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (request.Status is null || !ReportStatus.All.Contains(request.Status) ||
            (request.Note?.Length ?? 0) > DamageReportRules.MaxNoteLength ||
            (request.InternalNote?.Length ?? 0) > DamageReportRules.MaxNoteLength)
        {
            return ApiError.BadRequest().ToHttpResult();
        }

        if (!context.Request.HasIfMatch())
        {
            return ApiError.PreconditionRequired().ToHttpResult();
        }

        // Kilit + If-Match: iki personelin eşzamanlı kararı birbirini ezmez.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var report = await db.Reports
            .FromSql($"SELECT * FROM damage_reports WHERE id = {id} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (report is null)
        {
            return ApiError.NotFound().ToHttpResult();
        }

        if (context.Request.CheckIfMatch(StaffReportResponse.ETagOf(report)) is { } precondition)
        {
            return precondition.ToHttpResult();
        }

        var previous = report.Status;
        var now = clock.GetUtcNow();
        if (!report.MoveTo(request.Status, request.Note, now, request.InternalNote, context.User.RequiredUserId(),
                context.User.DisplayName()))
        {
            return ApiError.Conflict(ErrorCodes.InvalidTransition).ToHttpResult();
        }

        if (report.IsFinal)
        {
            await db.Locations.Where(l => l.ReportId == id)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.DeleteAfter, now + DamageReportRules.LocationRetention), cancellationToken);
        }

        audit.Record("report.status-changed", "damage-report", report.Id, new Dictionary<string, string?>
        {
            ["referenceCode"] = report.ReferenceCode,
            ["status.from"] = previous,
            ["status.to"] = report.Status,
        });
        events.Publish(new DamageReportStatusChanged(Guid.CreateVersion7(now), now, report.Id, report.OwnerId, report.Status));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var location = await db.Locations.AsNoTracking().SingleOrDefaultAsync(l => l.ReportId == id, cancellationToken);
        var monuments = await catalog.GetAsync([report.MonumentId], RequestLanguage.Resolve(context.Request), cancellationToken);
        context.Response.SetETag(StaffReportResponse.ETagOf(report));
        return TypedResults.Ok(StaffReportResponse.From(report, monuments.GetValueOrDefault(report.MonumentId), location, storage,
            options.Value));
    }

    private static async Task<IResult> Summary(CivilProtectionDbContext db, CancellationToken cancellationToken)
    {
        var byStatus = await db.Reports.AsNoTracking().GroupBy(r => r.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
        var openStatuses = new[] { ReportStatus.Received, ReportStatus.InReview, ReportStatus.Forwarded };
        var byUrgency = await db.Reports.AsNoTracking().Where(r => openStatuses.Contains(r.Status)).GroupBy(r => r.Urgency)
            .Select(g => new { Urgency = g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
        var byEvidence = await db.Reports.AsNoTracking().Where(r => openStatuses.Contains(r.Status)).GroupBy(r => r.EvidenceLevel)
            .Select(g => new { Level = g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
        var statusCounts = ReportStatus.All.Order(StringComparer.Ordinal)
            .ToDictionary(s => s, s => byStatus.SingleOrDefault(x => x.Status == s)?.Count ?? 0);
        return TypedResults.Ok(new StaffReportSummary(
            statusCounts.Values.Sum(),
            statusCounts.Where(x => ReportStatus.IsOpen(x.Key)).Sum(x => x.Value),
            statusCounts,
            UrgencyOrder.ToDictionary(u => u, u => byUrgency.SingleOrDefault(x => x.Urgency == u)?.Count ?? 0),
            new[] { EvidenceLevel.Strong, EvidenceLevel.Unverified, EvidenceLevel.Suspicious, EvidenceLevel.Pending }
                .ToDictionary(l => l, l => byEvidence.SingleOrDefault(x => (x.Level ?? EvidenceLevel.Pending) == l)?.Count ?? 0)));
    }
}
