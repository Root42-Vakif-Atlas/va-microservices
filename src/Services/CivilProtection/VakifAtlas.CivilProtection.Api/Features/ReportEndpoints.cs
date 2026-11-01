using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using VakifAtlas.CatalogClient;
using VakifAtlas.CivilProtection.Api.Infrastructure;
using VakifAtlas.CivilProtection.Domain;
using VakifAtlas.Contracts;
using VakifAtlas.Media;
using VakifAtlas.Messaging;
using VakifAtlas.ServiceDefaults.Auth;
using VakifAtlas.ServiceDefaults.Http;
using VakifAtlas.ServiceDefaults.Redis;
using VakifAtlas.SharedKernel;

namespace VakifAtlas.CivilProtection.Api.Features;

/// <summary>
/// Sivil Koruma (ADR 35): tüm uçlar Bearer ister; kullanıcı yalnızca kendi raporlarını görür
/// (başkasının raporu 404). Konum yalnızca açık onayla ve yalnızca bu raporda gönderilir.
/// </summary>
public static class ReportEndpoints
{
    private const int DefaultLimit = 20;
    private const int MaxLimit = 50;

    public static RouteGroupBuilder MapReportEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("reports", List);
        group.MapGet("reports/{id}", Detail);
        group.MapPost("photo-uploads", RequestUpload);
        group.MapPost("reports", Submit);
        return group.RequireAuthorization();
    }

    private static async Task<IResult> List(HttpContext context, CivilProtectionDbContext db, ICatalogClient catalog,
        IObjectStorage storage, IOptions<StorageOptions> options, CancellationToken cancellationToken)
    {
        var limit = QueryParsing.Limit(context.Request.Query["limit"], DefaultLimit, MaxLimit);
        if (limit is null)
        {
            return ApiError.BadRequest().ToHttpResult();
        }

        var userId = context.User.RequiredUserId();
        var own = db.Reports.AsNoTracking().Where(r => r.OwnerId == userId);
        var total = await own.CountAsync(cancellationToken);
        if (context.Request.Query["cursor"].ToString() is { Length: > 0 } cursor)
        {
            if (Cursor.Parse(cursor) is not var (createdAt, id))
            {
                return ApiError.BadRequest(ErrorCodes.InvalidCursor).ToHttpResult();
            }

            own = own.Where(r => r.CreatedAt < createdAt || (r.CreatedAt == createdAt && string.Compare(r.Id, id) < 0));
        }

        var reports = await own.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id).Take(limit.Value + 1)
            .ToListAsync(cancellationToken);
        var next = reports.Count > limit ? Cursor.Of(reports[limit.Value - 1]) : null;
        var page = reports.Take(limit.Value).ToList();
        var monuments = await catalog.GetAsync(page.Select(r => r.MonumentId), RequestLanguage.Resolve(context.Request), cancellationToken);
        context.Response.Headers.CacheControl = "private, no-store";
        return TypedResults.Ok(new Page<ReportResponse>(
            page.Select(r => ReportResponse.From(r, monuments.GetValueOrDefault(r.MonumentId), storage, options.Value)).ToArray(),
            total, next));
    }

    private static async Task<IResult> Detail(string id, HttpContext context, CivilProtectionDbContext db, ICatalogClient catalog,
        IObjectStorage storage, IOptions<StorageOptions> options, CancellationToken cancellationToken)
    {
        var userId = context.User.RequiredUserId();
        var report = await db.Reports.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id && r.OwnerId == userId, cancellationToken);
        if (report is null)
        {
            return ApiError.NotFound().ToHttpResult();
        }

        var monuments = await catalog.GetAsync([report.MonumentId], RequestLanguage.Resolve(context.Request), cancellationToken);
        context.Response.Headers.CacheControl = "private, no-store";
        return TypedResults.Ok(ReportResponse.From(report, monuments.GetValueOrDefault(report.MonumentId), storage, options.Value));
    }

    private static async Task<IResult> RequestUpload(PhotoUploadRequest request, ClaimsPrincipal principal,
        PhotoUploadService<CivilProtectionDbContext> uploads, CancellationToken cancellationToken)
    {
        var ticket = await uploads.CreateTicketAsync(principal.RequiredUserId(), "civil-protection", request, cancellationToken);
        return ticket.ToHttpResult(value => TypedResults.Created((string?)null, value));
    }

    private static async Task<IResult> Submit(SubmitReportRequest request, HttpContext context, CivilProtectionDbContext db,
        ICatalogClient catalog, PhotoUploadService<CivilProtectionDbContext> uploads, IObjectStorage storage,
        IOptions<StorageOptions> options, IEventPublisher events, IUsageLimiter limiter, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var userId = context.User.RequiredUserId();
        var location = request.Location;
        if (request.MonumentId is null || request.Category is null || !DamageReportRules.Categories.Contains(request.Category) ||
            request.Urgency is null || !DamageReportRules.Urgencies.Contains(request.Urgency) ||
            !DamageReportRules.IsValidDescription(request.Description) ||
            (location is not null && (location.Latitude is not { } lat || location.Longitude is not { } lng ||
                                      location.AccuracyMeters is not { } accuracy ||
                                      !DamageReportRules.IsValidLocation(lat, lng, accuracy))))
        {
            return ApiError.BadRequest(ErrorCodes.InvalidReport).ToHttpResult();
        }

        var language = RequestLanguage.Resolve(context.Request);
        var monument = (await catalog.GetAsync([request.MonumentId], language, cancellationToken)).GetValueOrDefault(request.MonumentId);
        if (monument is null)
        {
            return ApiError.BadRequest(ErrorCodes.InvalidReport).ToHttpResult();
        }

        var quota = await limiter.TryConsumeAsync($"damage-report:{userId}", DamageReportRules.MaxPerHour, TimeSpan.FromHours(1),
            cancellationToken);
        if (!quota.Allowed)
        {
            return ApiError.TooManyRequests(quota.RetryAfterSeconds).ToHttpResult();
        }

        var id = Ids.New("rpt");
        var photos = await uploads.ClaimAsync(userId, request.Photos ?? [], options.Value.Buckets.Private, $"civil-protection/{id}",
            ErrorCodes.InvalidReport, cancellationToken);
        if (!photos.IsSuccess)
        {
            return photos.Error.ToHttpResult();
        }

        var now = clock.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var check = location is null
            ? LocationCheck.NotProvided
            : LocationCheck.Evaluate(new GeoPoint(location.Latitude!.Value, location.Longitude!.Value), location.AccuracyMeters!.Value,
                monument.Point);
        var report = DamageReport.Submit(id, await ReferenceCodes.NextAsync(db, TurkeyTime.YearOf(now), cancellationToken), userId,
            monument.Id, request.Category, request.Urgency, request.Description!.Trim(),
            photos.Value.Select(p => new ReportPhoto(p.Id, p.Key, p.ThumbnailKey, p.Width, p.Height, p.Description)), check, now);
        db.Reports.Add(report);
        if (location is not null)
        {
            db.Locations.Add(new ReportLocation
            {
                ReportId = id,
                Latitude = location.Latitude!.Value,
                Longitude = location.Longitude!.Value,
                AccuracyMeters = location.AccuracyMeters!.Value,
            });
        }

        events.Publish(new DamageReportSubmitted(Guid.NewGuid(), now, userId, id, new ContributionPlace(monument.Id, monument.City)));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TypedResults.Created($"civil-protection/reports/{id}", ReportResponse.From(report, monument, storage, options.Value));
    }

    internal static class Cursor
    {
        public static string Of(DamageReport report) => CursorCodec.EncodeInstantId(report.CreatedAt, report.Id);

        public static (DateTimeOffset CreatedAt, string Id)? Parse(string cursor) => CursorCodec.DecodeInstantId(cursor);
    }
}
