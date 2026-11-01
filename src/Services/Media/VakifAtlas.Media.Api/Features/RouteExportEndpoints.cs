using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using VakifAtlas.Media;
using VakifAtlas.Media.Api.Infrastructure;
using VakifAtlas.Media.Domain;
using VakifAtlas.ServiceDefaults.Http;
using VakifAtlas.SharedKernel;

namespace VakifAtlas.Media.Api.Features;

/// <summary>Request to start a route export.</summary>
/// <param name="RouteId">Identifier of the route.</param>
/// <param name="RouteName">Display name of the route.</param>
/// <param name="FileName">File name of the PDF, ending in <c>.pdf</c>.</param>
/// <param name="ByteSize">Size of the PDF in bytes.</param>
/// <param name="StopCount">Number of stops on the route.</param>
/// <param name="Sha256">Hexadecimal SHA-256 of the PDF.</param>
public sealed record CreateExportRequest(string? RouteId, string? RouteName, string? FileName, long? ByteSize, int? StopCount,
    string? Sha256);

/// <summary>Upload ticket for a new export.</summary>
/// <param name="Id">Export identifier.</param>
/// <param name="UploadUrl">Presigned <c>PUT</c> URL to upload the PDF to.</param>
/// <param name="UploadHeaders">Extra headers the upload request must send.</param>
/// <param name="ExpiresAt">When the upload URL stops working.</param>
public sealed record ExportUploadResponse(string Id, string UploadUrl, IReadOnlyDictionary<string, string> UploadHeaders,
    DateTimeOffset ExpiresAt);

/// <summary>Representation of an active export.</summary>
/// <param name="Id">Export identifier.</param>
/// <param name="RouteId">Identifier of the exported route.</param>
/// <param name="RouteName">Display name of the exported route.</param>
/// <param name="FileName">File name offered on download.</param>
/// <param name="ByteSize">Size of the PDF in bytes.</param>
/// <param name="StopCount">Number of stops on the route.</param>
/// <param name="CreatedAt">When the export was created.</param>
public sealed record ExportResponse(string Id, string RouteId, string RouteName, string FileName, long ByteSize, int StopCount,
    DateTimeOffset CreatedAt)
{
    /// <summary>Maps a stored export to its response.</summary>
    /// <param name="export">The stored export.</param>
    public static ExportResponse From(RouteExport export) =>
        new(export.Id, export.RouteId, export.RouteName, export.FileName, export.ByteSize, export.StopCount, export.CreatedAt);
}

/// <summary>A presigned link to a PDF.</summary>
/// <param name="Url">The presigned <c>GET</c> URL.</param>
/// <param name="ExpiresAt">When the URL stops working.</param>
public sealed record DownloadResponse(string Url, DateTimeOffset ExpiresAt);

/// <summary>
/// Route PDF exports (api-contracts.md → Route exports). Signing in is not required: the owner is the account
/// when signed in, otherwise the installation (<see cref="ExportOwner"/>). Records are only visible to their
/// owner (someone else's yields 404). At most 50 per owner; beyond that the oldest is deleted.
/// </summary>
public static class RouteExportEndpoints
{
    /// <summary>Maps the route export endpoints.</summary>
    /// <param name="group">The <c>route-exports</c> route group.</param>
    public static RouteGroupBuilder MapRouteExportEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost(string.Empty, Create);
        group.MapPost("{id}/complete", Complete);
        group.MapGet(string.Empty, List);
        group.MapGet("{id}/download", Download);
        group.MapPost("{id}/share-link", ShareLink);
        group.MapDelete("{id}", Delete);
        return group;
    }

    private static async Task<IResult> Create(CreateExportRequest request, HttpContext context, MediaDbContext db,
        IObjectStorage storage, IOptions<StorageOptions> options, TimeProvider clock, CancellationToken cancellationToken)
    {
        var resolved = await ExportOwner.ResolveAsync(context, db, storage, options, cancellationToken);
        if (!resolved.IsSuccess)
        {
            return resolved.Error.ToHttpResult();
        }

        if (!RouteExportRules.IsValid(request.RouteId, request.RouteName, request.FileName, request.ByteSize, request.StopCount,
                request.Sha256))
        {
            return ApiError.BadRequest(ErrorCodes.InvalidExport).ToHttpResult();
        }

        var owner = resolved.Value;
        var now = clock.GetUtcNow();
        var pendingSince = now - RouteExportRules.UploadLifetime;
        var pending = await owner.Exports(db.Exports)
            .CountAsync(e => e.Status == ExportStatus.Pending && e.CreatedAt > pendingSince, cancellationToken);
        if (pending >= RouteExportRules.MaxPendingPerOwner)
        {
            return ApiError.TooManyRequests((int)RouteExportRules.UploadLifetime.TotalSeconds).ToHttpResult();
        }

        var export = new RouteExport
        {
            Id = Ids.New("exp"),
            UserId = owner.UserId,
            InstallationId = owner.Installation,
            RouteId = request.RouteId!,
            RouteName = request.RouteName!.Trim(),
            FileName = request.FileName!,
            ByteSize = request.ByteSize!.Value,
            StopCount = request.StopCount!.Value,
            Sha256 = request.Sha256!.ToLowerInvariant(),
            // Independent of the owner: the object stays in place when a guest record moves to an account.
            ObjectKey = $"{Guid.NewGuid():N}.pdf",
            CreatedAt = now,
        };
        db.Exports.Add(export);
        await db.SaveChangesAsync(cancellationToken);

        var url = storage.PresignPut(options.Value.Buckets.Exports, export.ObjectKey, RouteExportRules.ContentType,
            RouteExportRules.UploadLifetime);
        return TypedResults.Created($"route-exports/{export.Id}",
            new ExportUploadResponse(export.Id, url, new Dictionary<string, string>(), now + RouteExportRules.UploadLifetime));
    }

    /// <summary>
    /// Finishes an upload: verifies the size, PDF signature and SHA-256, activates the record and applies the
    /// quota. An upload that fails verification is deleted.
    /// </summary>
    private static async Task<IResult> Complete(string id, HttpContext context, MediaDbContext db, IObjectStorage storage,
        IOptions<StorageOptions> options, TimeProvider clock, CancellationToken cancellationToken)
    {
        var resolved = await ExportOwner.ResolveAsync(context, db, storage, options, cancellationToken);
        if (!resolved.IsSuccess)
        {
            return resolved.Error.ToHttpResult();
        }

        var owner = resolved.Value;
        var export = await owner.Exports(db.Exports).SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (export is null)
        {
            return ApiError.NotFound().ToHttpResult();
        }

        if (export.Status == ExportStatus.Active)
        {
            return TypedResults.Ok(ExportResponse.From(export));
        }

        var bucket = options.Value.Buckets.Exports;
        var head = await storage.HeadAsync(bucket, export.ObjectKey, cancellationToken);
        if (head is null || head.Size != export.ByteSize)
        {
            return ApiError.BadRequest(ErrorCodes.InvalidUpload).ToHttpResult();
        }

        await using (var content = await storage.GetAsync(bucket, export.ObjectKey, cancellationToken))
        {
            var bytes = new byte[5];
            var valid = content is not null &&
                        await content.Body.ReadAtLeastAsync(bytes, 5, throwOnEndOfStream: false, cancellationToken) == 5 &&
                        RouteExportRules.HasPdfSignature(bytes);
            if (valid)
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                hash.AppendData(bytes);
                var buffer = new byte[81920];
                int read;
                while ((read = await content!.Body.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                }

                valid = Convert.ToHexStringLower(hash.GetHashAndReset()) == export.Sha256;
            }

            if (!valid)
            {
                await storage.DeleteAsync(bucket, export.ObjectKey, cancellationToken);
                db.Exports.Remove(export);
                await db.SaveChangesAsync(cancellationToken);
                return ApiError.BadRequest(ErrorCodes.InvalidUpload).ToHttpResult();
            }
        }

        export.Status = ExportStatus.Active;
        export.CompletedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        await owner.TrimToQuotaAsync(db, storage, bucket, cancellationToken);
        return TypedResults.Ok(ExportResponse.From(export));
    }

    private static async Task<IResult> List(HttpContext context, MediaDbContext db, IObjectStorage storage,
        IOptions<StorageOptions> options, CancellationToken cancellationToken)
    {
        var resolved = await ExportOwner.ResolveAsync(context, db, storage, options, cancellationToken);
        if (!resolved.IsSuccess)
        {
            return resolved.Error.ToHttpResult();
        }

        var limit = QueryParsing.Limit(context.Request.Query["limit"], 20, RouteExportRules.MaxPerOwner);
        if (limit is null)
        {
            return ApiError.BadRequest().ToHttpResult();
        }

        var own = resolved.Value.Exports(db.Exports.AsNoTracking()).Where(e => e.Status == ExportStatus.Active);
        var total = await own.CountAsync(cancellationToken);
        if (context.Request.Query["cursor"].ToString() is { Length: > 0 } cursor)
        {
            if (CursorCodec.DecodeInstantId(cursor) is not { } position)
            {
                return ApiError.BadRequest(ErrorCodes.InvalidCursor).ToHttpResult();
            }

            var (createdAt, id) = position;

            own = own.Where(e => e.CreatedAt < createdAt || (e.CreatedAt == createdAt && string.Compare(e.Id, id) < 0));
        }

        var items = await own.OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id).Take(limit.Value + 1)
            .ToListAsync(cancellationToken);
        var next = items.Count > limit
            ? CursorCodec.EncodeInstantId(items[limit.Value - 1].CreatedAt, items[limit.Value - 1].Id)
            : null;
        context.Response.Headers.CacheControl = "private, no-store";
        return TypedResults.Ok(new Page<ExportResponse>(items.Take(limit.Value).Select(ExportResponse.From).ToArray(), total, next));
    }

    private static async Task<IResult> Download(string id, HttpContext context, MediaDbContext db, IObjectStorage storage,
        IOptions<StorageOptions> options, TimeProvider clock, CancellationToken cancellationToken)
    {
        var resolved = await ExportOwner.ResolveAsync(context, db, storage, options, cancellationToken);
        if (!resolved.IsSuccess)
        {
            return resolved.Error.ToHttpResult();
        }

        var export = await resolved.Value.Exports(db.Exports.AsNoTracking())
            .SingleOrDefaultAsync(e => e.Id == id && e.Status == ExportStatus.Active, cancellationToken);
        if (export is null)
        {
            return ApiError.NotFound().ToHttpResult();
        }

        var url = storage.PresignGet(options.Value.Buckets.Exports, export.ObjectKey, RouteExportRules.DownloadLifetime, export.FileName);
        return TypedResults.Ok(new DownloadResponse(url, clock.GetUtcNow() + RouteExportRules.DownloadLifetime));
    }

    /// <summary>
    /// Creates a shareable link: a presigned <c>GET</c> valid for 7 days that opens in the browser
    /// (<c>inline</c>). Anyone holding the link can open the PDF; the server does not store the link and
    /// generates a new one on every call.
    /// </summary>
    private static async Task<IResult> ShareLink(string id, HttpContext context, MediaDbContext db, IObjectStorage storage,
        IOptions<StorageOptions> options, TimeProvider clock, CancellationToken cancellationToken)
    {
        var resolved = await ExportOwner.ResolveAsync(context, db, storage, options, cancellationToken);
        if (!resolved.IsSuccess)
        {
            return resolved.Error.ToHttpResult();
        }

        var export = await resolved.Value.Exports(db.Exports.AsNoTracking())
            .SingleOrDefaultAsync(e => e.Id == id && e.Status == ExportStatus.Active, cancellationToken);
        if (export is null)
        {
            return ApiError.NotFound().ToHttpResult();
        }

        var url = storage.PresignGet(options.Value.Buckets.Exports, export.ObjectKey, RouteExportRules.ShareLinkLifetime,
            export.FileName, displayInline: true);
        context.Response.Headers.CacheControl = "private, no-store";
        return TypedResults.Ok(new DownloadResponse(url, clock.GetUtcNow() + RouteExportRules.ShareLinkLifetime));
    }

    private static async Task<IResult> Delete(string id, HttpContext context, MediaDbContext db, IObjectStorage storage,
        IOptions<StorageOptions> options, CancellationToken cancellationToken)
    {
        var resolved = await ExportOwner.ResolveAsync(context, db, storage, options, cancellationToken);
        if (!resolved.IsSuccess)
        {
            return resolved.Error.ToHttpResult();
        }

        var export = await resolved.Value.Exports(db.Exports).SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (export is null)
        {
            return ApiError.NotFound().ToHttpResult();
        }

        await storage.DeleteAsync(options.Value.Buckets.Exports, export.ObjectKey, cancellationToken);
        db.Exports.Remove(export);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }
}
