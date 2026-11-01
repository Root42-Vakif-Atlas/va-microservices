using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using VakifAtlas.CatalogClient;
using VakifAtlas.Contracts;
using VakifAtlas.Media;
using VakifAtlas.Memories.Api.Infrastructure;
using VakifAtlas.Memories.Domain;
using VakifAtlas.Messaging;
using VakifAtlas.ServiceDefaults.Auth;
using VakifAtlas.ServiceDefaults.Http;
using VakifAtlas.ServiceDefaults.Redis;
using VakifAtlas.SharedKernel;

namespace VakifAtlas.Memories.Api.Features;

/// <summary>
/// Anı Köşesi (ADR 34): okuma herkese açık; paylaşma, silme ve bildirme Bearer ister.
/// Fotoğraflar üç adımda yüklenir (bilet → ön imzalı PUT → anı oluşturma).
/// </summary>
public static class MemoryEndpoints
{
    private const int DefaultLimit = 20;
    private const int MaxLimit = 50;

    public static RouteGroupBuilder MapMemoryEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet(string.Empty, List).AllowAnonymous();
        group.MapGet("mine", Mine).RequireAuthorization();
        group.MapPost("photo-uploads", RequestUpload).RequireAuthorization();
        group.MapPost(string.Empty, Create).RequireAuthorization();
        group.MapDelete("{id}", Delete).RequireAuthorization();
        group.MapPost("{id}/reports", Report).RequireAuthorization();
        return group;
    }

    private static async Task<IResult> List(HttpContext context, MemoriesDbContext db, IOptions<StorageOptions> storage,
        CancellationToken cancellationToken)
    {
        var query = context.Request.Query;
        var monumentId = query["monumentId"].ToString();
        var limit = QueryParsing.Limit(query["limit"], DefaultLimit, MaxLimit);
        if (string.IsNullOrEmpty(monumentId) || limit is null ||
            !QueryParsing.TryDate(query["from"], out var from) || !QueryParsing.TryDate(query["to"], out var to) ||
            (from is not null && to is not null && from > to))
        {
            return ApiError.BadRequest().ToHttpResult();
        }

        var viewer = context.User.UserId();
        var visible = db.Memories.AsNoTracking().Where(m => m.MonumentId == monumentId && m.Status == MemoryStatus.Approved &&
                                                            m.ReportCount < MemoryRules.HideAfterReports);
        if (viewer is not null)
        {
            // Bildiren, bildirdiği anıyı artık görmez.
            visible = visible.Where(m => !db.Reports.Any(r => r.MemoryId == m.Id && r.ReporterId == viewer));
        }

        // Yıl sayıları tarih filtresinden bağımsızdır.
        MemoryFacets? facets = null;
        if (query["include"].ToString() == "facets")
        {
            var years = await visible.GroupBy(m => m.MemoryDate.Year)
                .Select(g => new { Year = g.Key, Count = g.Count() })
                .ToListAsync(cancellationToken);
            facets = new MemoryFacets(years.OrderByDescending(y => y.Year).Select(y => new YearCount(y.Year, y.Count)).ToArray());
        }

        var filtered = visible;
        if (from is { } start)
        {
            filtered = filtered.Where(m => m.MemoryDate >= start);
        }

        if (to is { } end)
        {
            filtered = filtered.Where(m => m.MemoryDate <= end);
        }

        var total = await filtered.CountAsync(cancellationToken);
        var page = filtered;
        if (query["cursor"].ToString() is { Length: > 0 } cursor)
        {
            if (MemoryCursors.ParseTimeline(cursor) is not var (date, createdAt, id))
            {
                return ApiError.BadRequest(ErrorCodes.InvalidCursor).ToHttpResult();
            }

            page = page.Where(m => m.MemoryDate < date ||
                                   (m.MemoryDate == date && (m.CreatedAt < createdAt ||
                                                             (m.CreatedAt == createdAt && string.Compare(m.Id, id) < 0))));
        }

        var items = await page.OrderByDescending(m => m.MemoryDate).ThenByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id)
            .Take(limit.Value + 1).ToListAsync(cancellationToken);
        var next = items.Count > limit ? MemoryCursors.Timeline(items[limit.Value - 1]) : null;

        context.Response.Headers.CacheControl = viewer is null ? "public, max-age=60" : "private, no-store";
        context.Response.Headers.Vary = "Authorization";
        return TypedResults.Ok(new MemoryPage(items.Take(limit.Value).Select(m => MemoryResponse.From(m, storage.Value)).ToArray(),
            total, next, facets));
    }

    /// <summary>"Anılarım": tüm durumlar (bildirilerek gizlenenler dahil), en son paylaşılan önce.</summary>
    private static async Task<IResult> Mine(HttpContext context, MemoriesDbContext db, IOptions<StorageOptions> storage,
        CancellationToken cancellationToken)
    {
        var limit = QueryParsing.Limit(context.Request.Query["limit"], DefaultLimit, MaxLimit);
        if (limit is null)
        {
            return ApiError.BadRequest().ToHttpResult();
        }

        var userId = context.User.RequiredUserId();
        var own = db.Memories.AsNoTracking().Where(m => m.AuthorId == userId);
        var total = await own.CountAsync(cancellationToken);
        if (context.Request.Query["cursor"].ToString() is { Length: > 0 } cursor)
        {
            if (MemoryCursors.ParseShared(cursor) is not var (createdAt, id))
            {
                return ApiError.BadRequest(ErrorCodes.InvalidCursor).ToHttpResult();
            }

            own = own.Where(m => m.CreatedAt < createdAt || (m.CreatedAt == createdAt && string.Compare(m.Id, id) < 0));
        }

        var items = await own.OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id)
            .Take(limit.Value + 1).ToListAsync(cancellationToken);
        var next = items.Count > limit ? MemoryCursors.Shared(items[limit.Value - 1]) : null;
        context.Response.Headers.CacheControl = "private, no-store";
        return TypedResults.Ok(new MemoryPage(items.Take(limit.Value).Select(m => MemoryResponse.From(m, storage.Value)).ToArray(),
            total, next, null));
    }

    private static async Task<IResult> RequestUpload(PhotoUploadRequest request, ClaimsPrincipal principal,
        PhotoUploadService<MemoriesDbContext> uploads, CancellationToken cancellationToken)
    {
        var ticket = await uploads.CreateTicketAsync(principal.RequiredUserId(), "memories", request, cancellationToken);
        return ticket.ToHttpResult(value => TypedResults.Created((string?)null, value));
    }

    private static async Task<IResult> Create(CreateMemoryRequest request, ClaimsPrincipal principal, MemoriesDbContext db,
        ICatalogClient catalog, PhotoUploadService<MemoriesDbContext> uploads, IModerationGateway moderation,
        IEventPublisher events, IUsageLimiter limiter, IOptions<StorageOptions> storage, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var userId = principal.RequiredUserId();
        var now = clock.GetUtcNow();
        DateOnly? visitedOn = null;
        if (request.MonumentId is null || !MemoryRules.IsValidText(request.Text) ||
            !QueryParsing.TryDate(request.VisitedOn, out visitedOn) || !MemoryRules.IsValidVisit(visitedOn, TurkeyTime.DateOf(now)) ||
            (request.Photos?.Count ?? 0) > PhotoRules.MaxPhotos)
        {
            return ApiError.BadRequest(ErrorCodes.InvalidMemory).ToHttpResult();
        }

        var monument = (await catalog.GetAsync([request.MonumentId], "tr", cancellationToken)).GetValueOrDefault(request.MonumentId);
        if (monument is null)
        {
            return ApiError.BadRequest(ErrorCodes.InvalidMemory).ToHttpResult();
        }

        var quota = await limiter.TryConsumeAsync($"memory-create:{userId}", MemoryRules.MaxPerHour, TimeSpan.FromHours(1),
            cancellationToken);
        if (!quota.Allowed)
        {
            return ApiError.TooManyRequests(quota.RetryAfterSeconds).ToHttpResult();
        }

        var id = Ids.New("mem");
        var photos = await uploads.ClaimAsync(userId, request.Photos ?? [], storage.Value.Buckets.Public, $"memories/{id}",
            ErrorCodes.InvalidMemory, cancellationToken);
        if (!photos.IsSuccess)
        {
            return photos.Error.ToHttpResult();
        }

        var memory = new Memory
        {
            Id = id,
            MonumentId = monument.Id,
            AuthorId = userId,
            AuthorName = MemoryRules.PublicDisplayName(principal.DisplayName()),
            Text = request.Text!.Trim(),
            VisitedOn = visitedOn,
            CreatedAt = now,
            MemoryDate = Memory.MemoryDateOf(visitedOn, now),
            Photos = photos.Value.Select(p => new MemoryPhoto(p.Id, p.Key, p.ThumbnailKey, p.Width, p.Height, p.Description)).ToList(),
        };
        // YZ modunda karar (onay / inceleme / kaldırma) bu istekte verilir; servis yanıt vermezse anı bekler.
        await moderation.ModerateAsync(memory, cancellationToken);
        db.Memories.Add(memory);
        if (memory.Status == MemoryStatus.Approved)
        {
            memory.PublishedAt ??= now;
            events.Publish(new MemoryPublished(Guid.NewGuid(), now, userId, memory.Id, new ContributionPlace(monument.Id, monument.City)));
        }

        if (memory.DecidedByAi)
        {
            events.Publish(new MemoryModerated(Guid.NewGuid(), now, memory.Id, userId,
                memory.Status == MemoryStatus.Approved ? ModerationDecision.Approve : ModerationDecision.Reject));
        }

        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Created($"memories/{memory.Id}", MemoryResponse.From(memory, storage.Value));
    }

    private static async Task<IResult> Delete(string id, ClaimsPrincipal principal, MemoriesDbContext db,
        PhotoUploadService<MemoriesDbContext> uploads, IOptions<StorageOptions> storage, CancellationToken cancellationToken)
    {
        var userId = principal.RequiredUserId();
        var memory = await db.Memories.SingleOrDefaultAsync(m => m.Id == id && m.AuthorId == userId, cancellationToken);
        if (memory is null)
        {
            return ApiError.NotFound().ToHttpResult();
        }

        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            await db.Reports.Where(r => r.MemoryId == id).ExecuteDeleteAsync(cancellationToken);
            db.Memories.Remove(memory);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        await uploads.DeleteAsync(storage.Value.Buckets.Public, Stored(memory), cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> Report(string id, ReportRequest request, ClaimsPrincipal principal, MemoriesDbContext db,
        IUsageLimiter limiter, TimeProvider clock, CancellationToken cancellationToken)
    {
        var userId = principal.RequiredUserId();
        var quota = await limiter.TryConsumeAsync($"memory-report:{userId}", MemoryRules.MaxReportsPerHour, TimeSpan.FromHours(1),
            cancellationToken);
        if (!quota.Allowed)
        {
            return ApiError.TooManyRequests(quota.RetryAfterSeconds).ToHttpResult();
        }

        var memory = await db.Memories.AsNoTracking().SingleOrDefaultAsync(m => m.Id == id, cancellationToken);
        if (memory is null)
        {
            return ApiError.NotFound().ToHttpResult();
        }

        if (memory.AuthorId == userId)
        {
            return ApiError.BadRequest(ErrorCodes.CannotReportOwn).ToHttpResult();
        }

        if (!MemoryRules.IsValidReport(request.Reason, request.Note))
        {
            return ApiError.BadRequest(ErrorCodes.InvalidReport).ToHttpResult();
        }

        // İdempotent: aynı kullanıcının tekrar bildirimi sayılmaz.
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO memory_reports (memory_id, reporter_id, reason, note, created_at)
            VALUES ({id}, {userId}, {request.Reason}, {note}, {clock.GetUtcNow()})
            ON CONFLICT DO NOTHING
            """, cancellationToken);
        await RecountReportsAsync(db, [id], cancellationToken);
        return TypedResults.Accepted((string?)null);
    }

    /// <summary>Yalnızca açık bildirimler sayılır: VGM'nin reddettiği bildirimler anıyı tekrar gizlemez.</summary>
    internal static Task RecountReportsAsync(MemoriesDbContext db, IReadOnlyCollection<string> memoryIds, CancellationToken cancellationToken) =>
        db.Memories.Where(m => memoryIds.Contains(m.Id))
            .ExecuteUpdateAsync(setters => setters.SetProperty(m => m.ReportCount,
                m => db.Reports.Count(r => r.MemoryId == m.Id && r.Resolution == ReportResolution.Open)), cancellationToken);

    internal static IEnumerable<StoredPhoto> Stored(Memory memory) =>
        memory.Photos.Select(p => new StoredPhoto(p.Id, p.Key, p.ThumbnailKey, p.Width, p.Height, p.Description));
}
