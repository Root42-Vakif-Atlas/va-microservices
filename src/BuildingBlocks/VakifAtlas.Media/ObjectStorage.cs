using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VakifAtlas.ServiceDefaults;

namespace VakifAtlas.Media;

/// <summary>Settings for the S3 compatible object storage.</summary>
public sealed class StorageOptions
{
    /// <summary>Configuration section name.</summary>
    public const string Section = "Storage";

    /// <summary>In-cluster S3 endpoint, for example <c>http://seaweedfs-s3:8333</c>.</summary>
    public string ServiceUrl { get; set; } = "http://localhost:8333";

    /// <summary>Client-facing S3 address used to sign presigned URLs, since the SigV4 signature covers the host.</summary>
    public string? PublicUrl { get; set; }

    /// <summary>S3 access key.</summary>
    public string AccessKey { get; set; } = string.Empty;

    /// <summary>S3 secret key.</summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>Region used for request signing.</summary>
    public string Region { get; set; } = "us-east-1";

    /// <summary>Creates missing buckets at startup, meant for dev and test only.</summary>
    public bool EnsureBuckets { get; set; }

    /// <summary>Client base URL for public objects such as catalog images and memory photos.</summary>
    public string PublicMediaBaseUrl { get; set; } = "http://localhost:8080/v1/media/";

    /// <summary>Bucket names used by the services.</summary>
    public BucketNames Buckets { get; set; } = new();
}

/// <summary>Names of the object storage buckets.</summary>
public sealed class BucketNames
{
    /// <summary>Temporary presigned PUT uploads, removed when they expire unclaimed.</summary>
    public string Uploads { get; set; } = "uploads";

    /// <summary>Public media served through the media service behind the CDN.</summary>
    public string Public { get; set; } = "media-public";

    /// <summary>Private media such as damage photos, read only through short-lived presigned GETs.</summary>
    public string Private { get; set; } = "media-private";

    /// <summary>Route PDF exports, private to their owner.</summary>
    public string Exports { get; set; } = "route-exports";

    /// <summary>Returns every configured bucket name.</summary>
    public IEnumerable<string> All() => [Uploads, Public, Private, Exports];
}

/// <summary>Metadata of a stored object.</summary>
/// <param name="Size">Object size in bytes.</param>
/// <param name="ContentType">MIME type of the object.</param>
/// <param name="ETag">Entity tag reported by the store.</param>
/// <param name="LastModified">Time the object was last modified.</param>
public sealed record StoredObject(long Size, string ContentType, string? ETag, DateTimeOffset? LastModified);

/// <summary>An object body stream together with its metadata.</summary>
/// <param name="Body">Stream of the object content.</param>
/// <param name="Metadata">Metadata of the object.</param>
public sealed record ObjectContent(Stream Body, StoredObject Metadata) : IAsyncDisposable
{
    /// <inheritdoc />
    public ValueTask DisposeAsync() => Body.DisposeAsync();
}

/// <summary>S3 compatible object storage such as SeaweedFS, Ceph RGW or an in-house S3.</summary>
public interface IObjectStorage
{
    /// <summary>Uploads an object.</summary>
    /// <param name="bucket">Target bucket.</param>
    /// <param name="key">Object key.</param>
    /// <param name="content">Content stream, left open after the upload.</param>
    /// <param name="contentType">MIME type of the content.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <param name="cacheControl">Optional Cache-Control header value.</param>
    Task PutAsync(string bucket, string key, Stream content, string contentType, CancellationToken cancellationToken,
        string? cacheControl = null);

    /// <summary>Gets object metadata, or null when the object does not exist.</summary>
    /// <param name="bucket">Source bucket.</param>
    /// <param name="key">Object key.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    Task<StoredObject?> HeadAsync(string bucket, string key, CancellationToken cancellationToken);

    /// <summary>Opens an object for reading, or returns null when it does not exist.</summary>
    /// <param name="bucket">Source bucket.</param>
    /// <param name="key">Object key.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    Task<ObjectContent?> GetAsync(string bucket, string key, CancellationToken cancellationToken);

    /// <summary>Reads the first bytes of an object to check its file signature.</summary>
    /// <param name="bucket">Source bucket.</param>
    /// <param name="key">Object key.</param>
    /// <param name="length">Number of bytes to read.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    Task<byte[]> ReadPrefixAsync(string bucket, string key, int length, CancellationToken cancellationToken);

    /// <summary>Deletes an object.</summary>
    /// <param name="bucket">Bucket holding the object.</param>
    /// <param name="key">Object key.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    Task DeleteAsync(string bucket, string key, CancellationToken cancellationToken);

    /// <summary>Deletes every object whose key starts with the given prefix.</summary>
    /// <param name="bucket">Bucket holding the objects.</param>
    /// <param name="prefix">Key prefix to match.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    Task DeletePrefixAsync(string bucket, string prefix, CancellationToken cancellationToken);

    /// <summary>Creates a presigned PUT URL on the public endpoint.</summary>
    /// <param name="bucket">Target bucket.</param>
    /// <param name="key">Object key.</param>
    /// <param name="contentType">MIME type the client must send.</param>
    /// <param name="lifetime">How long the URL stays valid.</param>
    string PresignPut(string bucket, string key, string contentType, TimeSpan lifetime);

    /// <summary>Creates a presigned GET URL on the public endpoint.</summary>
    /// <param name="bucket">Source bucket.</param>
    /// <param name="key">Object key.</param>
    /// <param name="lifetime">How long the URL stays valid.</param>
    /// <param name="downloadFileName">Optional file name set in the Content-Disposition header.</param>
    /// <param name="displayInline">Whether the browser should display the file instead of downloading it.</param>
    string PresignGet(string bucket, string key, TimeSpan lifetime, string? downloadFileName = null, bool displayInline = false);

    /// <summary>Creates the given buckets when they do not exist yet.</summary>
    /// <param name="buckets">Bucket names to ensure.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    Task EnsureBucketsAsync(IEnumerable<string> buckets, CancellationToken cancellationToken);
}

internal sealed class S3ObjectStorage : IObjectStorage, IDisposable
{
    private readonly AmazonS3Client _internal;
    private readonly AmazonS3Client _public;
    private readonly TimeProvider _clock;
    private readonly Protocol _publicProtocol;

    public S3ObjectStorage(IOptions<StorageOptions> options, TimeProvider clock)
    {
        _clock = clock;
        var settings = options.Value;
        var credentials = new BasicAWSCredentials(settings.AccessKey, settings.SecretKey);
        _internal = new AmazonS3Client(credentials, Config(settings.ServiceUrl, settings.Region));
        var publicUrl = settings.PublicUrl ?? settings.ServiceUrl;
        _public = new AmazonS3Client(credentials, Config(publicUrl, settings.Region));
        // The presigned URL scheme comes from the request, and the SDK defaults to HTTPS.
        _publicProtocol = publicUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? Protocol.HTTP : Protocol.HTTPS;
    }

    private static AmazonS3Config Config(string url, string region) => new()
    {
        ServiceURL = url,
        ForcePathStyle = true,
        AuthenticationRegion = region,
        UseHttp = url.StartsWith("http://", StringComparison.OrdinalIgnoreCase),
        RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
        ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
    };

    public async Task PutAsync(string bucket, string key, Stream content, string contentType, CancellationToken cancellationToken,
        string? cacheControl = null)
    {
        var request = new PutObjectRequest
        {
            BucketName = bucket,
            Key = key,
            InputStream = content,
            ContentType = contentType,
            AutoCloseStream = false,
        };
        if (cacheControl is not null)
        {
            request.Headers.CacheControl = cacheControl;
        }

        await _internal.PutObjectAsync(request, cancellationToken);
    }

    public async Task<StoredObject?> HeadAsync(string bucket, string key, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _internal.GetObjectMetadataAsync(bucket, key, cancellationToken);
            return new StoredObject(response.ContentLength, response.Headers.ContentType, response.ETag,
                response.LastModified is { } modified ? new DateTimeOffset(modified, TimeSpan.Zero) : null);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<ObjectContent?> GetAsync(string bucket, string key, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _internal.GetObjectAsync(bucket, key, cancellationToken);
            return new ObjectContent(response.ResponseStream, new StoredObject(response.ContentLength,
                response.Headers.ContentType, response.ETag,
                response.LastModified is { } modified ? new DateTimeOffset(modified, TimeSpan.Zero) : null));
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<byte[]> ReadPrefixAsync(string bucket, string key, int length, CancellationToken cancellationToken)
    {
        using var response = await _internal.GetObjectAsync(new GetObjectRequest
        {
            BucketName = bucket,
            Key = key,
            ByteRange = new ByteRange(0, length - 1),
        }, cancellationToken);
        var buffer = new byte[length];
        var read = await response.ResponseStream.ReadAtLeastAsync(buffer, length, throwOnEndOfStream: false, cancellationToken);
        return buffer[..read];
    }

    public Task DeleteAsync(string bucket, string key, CancellationToken cancellationToken) =>
        _internal.DeleteObjectAsync(bucket, key, cancellationToken);

    public async Task DeletePrefixAsync(string bucket, string prefix, CancellationToken cancellationToken)
    {
        var request = new ListObjectsV2Request { BucketName = bucket, Prefix = prefix };
        ListObjectsV2Response response;
        do
        {
            response = await _internal.ListObjectsV2Async(request, cancellationToken);
            foreach (var item in response.S3Objects ?? [])
            {
                await _internal.DeleteObjectAsync(bucket, item.Key, cancellationToken);
            }

            request.ContinuationToken = response.NextContinuationToken;
        }
        while (response.IsTruncated == true);
    }

    public string PresignPut(string bucket, string key, string contentType, TimeSpan lifetime) =>
        _public.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = bucket,
            Key = key,
            Verb = HttpVerb.PUT,
            Protocol = _publicProtocol,
            ContentType = contentType,
            Expires = _clock.GetUtcNow().Add(lifetime).UtcDateTime,
        });

    public string PresignGet(string bucket, string key, TimeSpan lifetime, string? downloadFileName = null, bool displayInline = false)
    {
        var request = new GetPreSignedUrlRequest
        {
            BucketName = bucket,
            Key = key,
            Verb = HttpVerb.GET,
            Protocol = _publicProtocol,
            Expires = _clock.GetUtcNow().Add(lifetime).UtcDateTime,
        };
        if (downloadFileName is not null)
        {
            request.ResponseHeaderOverrides.ContentDisposition =
                $"{(displayInline ? "inline" : "attachment")}; filename=\"{downloadFileName}\"; filename*=UTF-8''{Uri.EscapeDataString(downloadFileName)}";
        }

        return _public.GetPreSignedURL(request);
    }

    public async Task EnsureBucketsAsync(IEnumerable<string> buckets, CancellationToken cancellationToken)
    {
        var existing = (await _internal.ListBucketsAsync(cancellationToken)).Buckets?.Select(b => b.BucketName).ToHashSet()
            ?? [];
        foreach (var bucket in buckets.Where(b => !existing.Contains(b)))
        {
            try
            {
                await _internal.PutBucketAsync(bucket, cancellationToken);
            }
            catch (AmazonS3Exception exception) when (exception.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists")
            {
                // Another replica created it.
            }
        }
    }

    public void Dispose()
    {
        _internal.Dispose();
        _public.Dispose();
    }
}

internal sealed class StorageHealthCheck(IObjectStorage storage, IOptions<StorageOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await storage.HeadAsync(options.Value.Buckets.Public, ".health", cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception exception) when (exception is AmazonServiceException or HttpRequestException)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "Object storage unreachable", exception);
        }
    }
}

internal sealed class BucketInitializer(IObjectStorage storage, IOptions<StorageOptions> options,
    ILogger<BucketInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.EnsureBuckets)
        {
            return;
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await storage.EnsureBucketsAsync(options.Value.Buckets.All(), cancellationToken);
                return;
            }
            catch (Exception exception) when (attempt < 10 && exception is AmazonServiceException or HttpRequestException)
            {
                logger.StorageNotReady(attempt);
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Registration helpers for object storage.</summary>
public static class ObjectStorageExtensions
{
    /// <summary>Registers object storage, the photo processor, bucket setup and the storage health check.</summary>
    /// <param name="builder">The web application builder.</param>
    public static WebApplicationBuilder AddObjectStorage(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.Section));
        builder.Services.AddSingleton<IObjectStorage, S3ObjectStorage>();
        builder.Services.AddSingleton<PhotoProcessor>();
        builder.Services.AddHostedService<BucketInitializer>();
        builder.Services.AddHealthChecks().AddCheck<StorageHealthCheck>("object-storage", HealthStatus.Degraded, tags: [ServiceDefaultsExtensions.ReadyTag]);
        return builder;
    }
}
