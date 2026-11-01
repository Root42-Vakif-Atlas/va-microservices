using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VakifAtlas.SharedKernel;

namespace VakifAtlas.Media;

/// <summary>Presigned upload ticket that only its owner can consume, and only once.</summary>
public sealed class PhotoUpload
{
    /// <summary>Ticket identifier.</summary>
    public required string Id { get; init; }

    /// <summary>Identifier of the user who owns the ticket.</summary>
    public required string OwnerId { get; init; }

    /// <summary>Declared file size in bytes.</summary>
    public long ByteSize { get; init; }

    /// <summary>Declared image width in pixels.</summary>
    public int Width { get; init; }

    /// <summary>Declared image height in pixels.</summary>
    public int Height { get; init; }

    /// <summary>Key of the uploaded object in the uploads bucket.</summary>
    public required string ObjectKey { get; init; }

    /// <summary>Time the ticket was created.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Time after which the ticket can no longer be used.</summary>
    public DateTimeOffset ExpiresAt { get; init; }

    /// <summary>Time the ticket was consumed, or null if still open.</summary>
    public DateTimeOffset? ConsumedAt { get; set; }
}

/// <summary>Client request for a photo upload ticket.</summary>
/// <param name="ContentType">MIME type of the photo, which must be JPEG.</param>
/// <param name="ByteSize">File size in bytes.</param>
/// <param name="Width">Image width in pixels.</param>
/// <param name="Height">Image height in pixels.</param>
public sealed record PhotoUploadRequest(string? ContentType, long? ByteSize, int? Width, int? Height);

/// <summary>Upload ticket returned to the client.</summary>
/// <param name="Id">Ticket identifier to reference when attaching the photo.</param>
/// <param name="UploadUrl">Presigned PUT URL for the upload.</param>
/// <param name="UploadHeaders">Extra headers the client must send with the upload.</param>
/// <param name="ExpiresAt">Time the ticket expires.</param>
public sealed record PhotoUploadTicket(string Id, string UploadUrl, IReadOnlyDictionary<string, string> UploadHeaders,
    DateTimeOffset ExpiresAt);

/// <summary>A photo attached to content by its upload ticket.</summary>
/// <param name="UploadId">Identifier of the upload ticket.</param>
/// <param name="Description">Optional photo description.</param>
public sealed record PhotoAttachment(string? UploadId, string? Description);

/// <summary>A processed photo stored in its target bucket; callers turn keys into URLs.</summary>
/// <param name="Id">Photo identifier.</param>
/// <param name="Key">Object key of the full-size image.</param>
/// <param name="ThumbnailKey">Object key of the thumbnail.</param>
/// <param name="Width">Width of the full-size image in pixels.</param>
/// <param name="Height">Height of the full-size image in pixels.</param>
/// <param name="Description">Optional photo description.</param>
/// <param name="ByteSize">Size of the full-size image in bytes; 0 when rebuilt from stored metadata.</param>
public sealed record StoredPhoto(string Id, string Key, string ThumbnailKey, int Width, int Height, string? Description, long ByteSize = 0);

/// <summary>Limits and constants for photo uploads.</summary>
public static class PhotoRules
{
    /// <summary>The only accepted content type.</summary>
    public const string JpegContentType = "image/jpeg";

    /// <summary>Maximum upload size in bytes.</summary>
    public const long MaxBytes = 5 * 1024 * 1024;

    /// <summary>Maximum unused, unexpired tickets per owner, which keeps storage from filling up.</summary>
    public const int MaxOpenTickets = 20;

    /// <summary>Maximum photos attached to a single item.</summary>
    public const int MaxPhotos = 4;

    /// <summary>Maximum length of a photo description.</summary>
    public const int MaxDescriptionLength = 250;

    /// <summary>Maximum declared width or height in pixels.</summary>
    public const int MaxPixels = 20_000;

    /// <summary>How long an upload ticket stays valid.</summary>
    public static readonly TimeSpan TicketLifetime = TimeSpan.FromMinutes(15);
}

/// <summary>EF Core model configuration for photo upload tickets.</summary>
public static class PhotoUploadModel
{
    /// <summary>Maps the photo upload ticket entity to its table.</summary>
    /// <param name="modelBuilder">The model builder to configure.</param>
    public static ModelBuilder AddPhotoUploads(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PhotoUpload>(entity =>
        {
            entity.ToTable("photo_uploads");
            entity.HasKey(u => u.Id);
            entity.Property(u => u.Id).HasColumnName("id").HasMaxLength(40);
            entity.Property(u => u.OwnerId).HasColumnName("owner_id").HasMaxLength(40);
            entity.Property(u => u.ByteSize).HasColumnName("byte_size");
            entity.Property(u => u.Width).HasColumnName("width");
            entity.Property(u => u.Height).HasColumnName("height");
            entity.Property(u => u.ObjectKey).HasColumnName("object_key").HasMaxLength(200);
            entity.Property(u => u.CreatedAt).HasColumnName("created_at");
            entity.Property(u => u.ExpiresAt).HasColumnName("expires_at");
            entity.Property(u => u.ConsumedAt).HasColumnName("consumed_at");
            entity.HasIndex(u => u.ExpiresAt).HasDatabaseName("ix_photo_uploads_pending").HasFilter("consumed_at IS NULL");
            entity.HasIndex(u => u.OwnerId).HasDatabaseName("ix_photo_uploads_owner_id");
        });
        return modelBuilder;
    }
}

/// <summary>Three-step photo upload: issue a ticket, let the client PUT directly to storage, then validate, re-encode and claim it.</summary>
/// <typeparam name="TDbContext">Database context that stores the upload tickets.</typeparam>
/// <param name="db">Database context holding the tickets.</param>
/// <param name="storage">Object storage client.</param>
/// <param name="processor">Photo re-encoder.</param>
/// <param name="options">Storage settings.</param>
/// <param name="clock">Time provider.</param>
/// <param name="logger">Logger instance.</param>
public sealed class PhotoUploadService<TDbContext>(
    TDbContext db,
    IObjectStorage storage,
    PhotoProcessor processor,
    IOptions<StorageOptions> options,
    TimeProvider clock,
    ILogger<PhotoUploadService<TDbContext>> logger)
    where TDbContext : DbContext
{
    /// <summary>Validates the request and issues a presigned upload ticket.</summary>
    /// <param name="ownerId">Identifier of the uploading user.</param>
    /// <param name="scope">Key prefix that groups the upload.</param>
    /// <param name="request">Declared photo details.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    public async Task<Result<PhotoUploadTicket>> CreateTicketAsync(string ownerId, string scope, PhotoUploadRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ContentType != PhotoRules.JpegContentType || request.ByteSize is not > 0 ||
            request.Width is not (> 0 and <= PhotoRules.MaxPixels) || request.Height is not (> 0 and <= PhotoRules.MaxPixels))
        {
            return ApiError.BadRequest();
        }

        if (request.ByteSize > PhotoRules.MaxBytes)
        {
            return ApiError.BadRequest(ErrorCodes.PhotoTooLarge);
        }

        var now = clock.GetUtcNow();
        var open = await db.Set<PhotoUpload>()
            .CountAsync(u => u.OwnerId == ownerId && u.ConsumedAt == null && u.ExpiresAt > now, cancellationToken);
        if (open >= PhotoRules.MaxOpenTickets)
        {
            return ApiError.TooManyRequests((int)PhotoRules.TicketLifetime.TotalSeconds);
        }

        var upload = new PhotoUpload
        {
            Id = Ids.New("up"),
            OwnerId = ownerId,
            ByteSize = request.ByteSize.Value,
            Width = request.Width.Value,
            Height = request.Height.Value,
            ObjectKey = $"{scope}/{Ids.New("obj", 16)}.jpg",
            CreatedAt = now,
            ExpiresAt = now + PhotoRules.TicketLifetime,
        };
        db.Set<PhotoUpload>().Add(upload);
        await db.SaveChangesAsync(cancellationToken);

        var url = storage.PresignPut(options.Value.Buckets.Uploads, upload.ObjectKey, PhotoRules.JpegContentType,
            PhotoRules.TicketLifetime);
        return new PhotoUploadTicket(upload.Id, url, new Dictionary<string, string>(), upload.ExpiresAt);
    }

    /// <summary>Consumes the tickets and stores the photos, or consumes none if any attachment is invalid.</summary>
    /// <param name="ownerId">Identifier of the user who owns the tickets.</param>
    /// <param name="attachments">Photos to claim.</param>
    /// <param name="targetBucket">Bucket the processed photos are written to.</param>
    /// <param name="targetPrefix">Key prefix for the processed photos.</param>
    /// <param name="invalidCode">Error code returned when any attachment is invalid.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    public async Task<Result<IReadOnlyList<StoredPhoto>>> ClaimAsync(string ownerId, IReadOnlyList<PhotoAttachment> attachments,
        string targetBucket, string targetPrefix, string invalidCode, CancellationToken cancellationToken)
    {
        if (attachments.Count == 0)
        {
            return Array.Empty<StoredPhoto>();
        }

        var ids = attachments.Select(a => a.UploadId).ToList();
        if (attachments.Count > PhotoRules.MaxPhotos || ids.Any(string.IsNullOrEmpty) || ids.Distinct().Count() != ids.Count ||
            attachments.Any(a => a.Description is { Length: > PhotoRules.MaxDescriptionLength }))
        {
            return ApiError.BadRequest(invalidCode);
        }

        var now = clock.GetUtcNow();
        var uploads = await db.Set<PhotoUpload>()
            .Where(u => ids.Contains(u.Id) && u.OwnerId == ownerId && u.ConsumedAt == null && u.ExpiresAt > now)
            .ToDictionaryAsync(u => u.Id, cancellationToken);
        if (uploads.Count != ids.Count)
        {
            return ApiError.BadRequest(invalidCode);
        }

        var uploadsBucket = options.Value.Buckets.Uploads;
        foreach (var upload in uploads.Values)
        {
            var head = await storage.HeadAsync(uploadsBucket, upload.ObjectKey, cancellationToken);
            if (head is null || head.Size != upload.ByteSize ||
                !PhotoProcessor.HasJpegSignature(await storage.ReadPrefixAsync(uploadsBucket, upload.ObjectKey, 2, cancellationToken)))
            {
                return ApiError.BadRequest(invalidCode);
            }
        }

        var stored = new List<StoredPhoto>(attachments.Count);
        foreach (var attachment in attachments)
        {
            var upload = uploads[attachment.UploadId!];
            await using var content = await storage.GetAsync(uploadsBucket, upload.ObjectKey, cancellationToken);
            var processed = content is null ? null : await processor.ProcessAsync(content.Body, upload.ByteSize, cancellationToken);
            if (processed is null)
            {
                await DeleteAsync(targetBucket, stored, cancellationToken);
                return ApiError.BadRequest(invalidCode);
            }

            var photoId = Ids.New("ph");
            var key = $"{targetPrefix}/{photoId}.jpg";
            var thumbnailKey = $"{targetPrefix}/{photoId}_{PhotoProcessor.ThumbnailWidth}.jpg";
            await PutAsync(targetBucket, key, processed.Full, cancellationToken);
            await PutAsync(targetBucket, thumbnailKey, processed.Thumbnail, cancellationToken);
            stored.Add(new StoredPhoto(photoId, key, thumbnailKey, processed.Width, processed.Height,
                string.IsNullOrWhiteSpace(attachment.Description) ? null : attachment.Description.Trim(), processed.Full.Length));
        }

        // Single use: only one of two concurrent requests can consume the tickets.
        var consumed = await db.Set<PhotoUpload>()
            .Where(u => ids.Contains(u.Id) && u.ConsumedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(u => u.ConsumedAt, now), cancellationToken);
        if (consumed != ids.Count)
        {
            await DeleteAsync(targetBucket, stored, cancellationToken);
            return ApiError.BadRequest(invalidCode);
        }

        foreach (var upload in uploads.Values)
        {
            await TryDeleteAsync(uploadsBucket, upload.ObjectKey, cancellationToken);
        }

        return stored;
    }

    /// <summary>Deletes stored photos and their thumbnails, logging failures instead of throwing.</summary>
    /// <param name="bucket">Bucket holding the photos.</param>
    /// <param name="photos">Photos to delete.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    public async Task DeleteAsync(string bucket, IEnumerable<StoredPhoto> photos, CancellationToken cancellationToken)
    {
        foreach (var photo in photos)
        {
            await TryDeleteAsync(bucket, photo.Key, cancellationToken);
            await TryDeleteAsync(bucket, photo.ThumbnailKey, cancellationToken);
        }
    }

    private async Task PutAsync(string bucket, string key, byte[] data, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(data);
        await storage.PutAsync(bucket, key, stream, PhotoRules.JpegContentType, cancellationToken,
            "public, max-age=31536000, immutable");
    }

    private async Task TryDeleteAsync(string bucket, string key, CancellationToken cancellationToken)
    {
        try
        {
            await storage.DeleteAsync(bucket, key, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.DeleteObjectFailed(exception, bucket, key);
        }
    }
}

/// <summary>Removes expired unclaimed uploads and old consumed tickets.</summary>
/// <typeparam name="TDbContext">Database context that stores the upload tickets.</typeparam>
/// <param name="scopes">Factory for per-run service scopes.</param>
/// <param name="storage">Object storage client.</param>
/// <param name="options">Storage settings.</param>
/// <param name="clock">Time provider.</param>
/// <param name="logger">Logger instance.</param>
public sealed class PhotoUploadCleanup<TDbContext>(
    IServiceScopeFactory scopes,
    IObjectStorage storage,
    IOptions<StorageOptions> options,
    TimeProvider clock,
    ILogger<PhotoUploadCleanup<TDbContext>> logger) : BackgroundService
    where TDbContext : DbContext
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10), clock);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<TDbContext>();
                var now = clock.GetUtcNow();
                var expired = await db.Set<PhotoUpload>()
                    .Where(u => u.ConsumedAt == null && u.ExpiresAt < now - TimeSpan.FromHours(1))
                    .Take(200).ToListAsync(stoppingToken);
                foreach (var upload in expired)
                {
                    await storage.DeleteAsync(options.Value.Buckets.Uploads, upload.ObjectKey, stoppingToken);
                }

                db.Set<PhotoUpload>().RemoveRange(expired);
                await db.SaveChangesAsync(stoppingToken);
                await db.Set<PhotoUpload>().Where(u => u.ConsumedAt < now - TimeSpan.FromDays(1))
                    .ExecuteDeleteAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.UploadCleanupFailed(exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

/// <summary>Registration helpers for photo uploads.</summary>
public static class PhotoUploadExtensions
{
    /// <summary>Registers the photo upload service and its cleanup job.</summary>
    /// <typeparam name="TDbContext">Database context that stores the upload tickets.</typeparam>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddPhotoUploads<TDbContext>(this IServiceCollection services) where TDbContext : DbContext
    {
        services.AddScoped<PhotoUploadService<TDbContext>>();
        services.AddHostedService<PhotoUploadCleanup<TDbContext>>();
        return services;
    }

    /// <summary>Builds the client URL of a public object served by the media service.</summary>
    /// <param name="options">Storage settings.</param>
    /// <param name="key">Object key.</param>
    public static string PublicUrl(this StorageOptions options, string key) =>
        $"{options.PublicMediaBaseUrl.TrimEnd('/')}/{key}";
}
