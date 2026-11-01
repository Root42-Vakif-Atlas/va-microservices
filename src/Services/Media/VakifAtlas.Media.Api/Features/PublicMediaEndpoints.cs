using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using VakifAtlas.Media;
using VakifAtlas.ServiceDefaults.Http;
using VakifAtlas.SharedKernel;

namespace VakifAtlas.Media.Api.Features;

/// <summary>
/// <c>GET media/{key}</c>: streams objects from the public bucket (catalog images, memory photos). Keys are
/// unguessable and never change, so responses are <c>immutable</c> with an ETag and sit behind a CDN. The
/// object storage itself is never exposed to clients.
/// </summary>
public static partial class PublicMediaEndpoints
{
    /// <summary>Maps the anonymous <c>GET</c> and <c>HEAD</c> endpoint for public media.</summary>
    /// <param name="group">The <c>media</c> route group.</param>
    public static RouteGroupBuilder MapPublicMediaEndpoints(this RouteGroupBuilder group)
    {
        group.MapMethods("{**key}", ["GET", "HEAD"], Get).AllowAnonymous();
        return group;
    }

    private static async Task<IResult> Get(string key, HttpContext context, IObjectStorage storage, IOptions<StorageOptions> options,
        CancellationToken cancellationToken)
    {
        if (!SafeKey().IsMatch(key) || key.Contains("..", StringComparison.Ordinal))
        {
            return ApiError.NotFound().ToHttpResult();
        }

        var bucket = options.Value.Buckets.Public;
        var head = await storage.HeadAsync(bucket, key, cancellationToken);
        if (head is null)
        {
            return ApiError.NotFound().ToHttpResult();
        }

        var etag = head.ETag is { Length: > 0 } value ? new EntityTagHeaderValue(value.StartsWith('"') ? value : $"\"{value}\"") : null;
        var response = context.Response;
        response.Headers.CacheControl = "public, max-age=31536000, immutable";
        response.Headers.XContentTypeOptions = "nosniff";
        if (etag is not null)
        {
            response.Headers.ETag = etag.ToString();
            if (context.Request.Headers.IfNoneMatch.ToString().Contains(etag.Tag.ToString(), StringComparison.Ordinal))
            {
                return TypedResults.StatusCode(StatusCodes.Status304NotModified);
            }
        }

        if (HttpMethods.IsHead(context.Request.Method))
        {
            response.ContentType = head.ContentType;
            response.ContentLength = head.Size;
            return TypedResults.Empty;
        }

        var content = await storage.GetAsync(bucket, key, cancellationToken);
        if (content is null)
        {
            return ApiError.NotFound().ToHttpResult();
        }

        context.Response.RegisterForDisposeAsync(content);
        return TypedResults.Stream(content.Body, head.ContentType, lastModified: head.LastModified);
    }

    [GeneratedRegex("^[A-Za-z0-9._/-]{1,300}$")]
    private static partial Regex SafeKey();
}
