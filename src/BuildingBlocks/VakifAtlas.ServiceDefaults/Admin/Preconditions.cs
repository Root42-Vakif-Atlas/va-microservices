using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using VakifAtlas.SharedKernel;

namespace VakifAtlas.ServiceDefaults.Admin;

/// <summary>Optimistic concurrency with <c>ETag</c> and <c>If-Match</c>.</summary>
public static class Preconditions
{
    /// <summary>Builds a strong <c>ETag</c> from the given field values.</summary>
    /// <param name="parts">Mutable field values, in a fixed order.</param>
    public static string ETagOf(params string?[] parts)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001f', parts.Select(p => p ?? "\u0000"))));
        return $"\"{Convert.ToHexStringLower(hash)[..20]}\"";
    }

    /// <summary>Sets the <c>ETag</c> response header.</summary>
    /// <param name="response">The response.</param>
    /// <param name="etag">Value from <see cref="ETagOf"/>.</param>
    public static void SetETag(this HttpResponse response, string etag) => response.Headers.ETag = etag;

    /// <summary>Returns 428 or 412 when <c>If-Match</c> is missing or stale, otherwise <c>null</c>.</summary>
    /// <param name="request">The modifying request.</param>
    /// <param name="currentETag">The resource's current <c>ETag</c>.</param>
    public static ApiError? CheckIfMatch(this HttpRequest request, string currentETag)
    {
        if (!request.HasIfMatch())
        {
            return ApiError.PreconditionRequired();
        }

        if (!EntityTagHeaderValue.TryParseList(request.Headers.IfMatch, out IList<EntityTagHeaderValue>? tags))
        {
            return ApiError.PreconditionFailed();
        }

        return tags.Any(tag => tag.Equals(EntityTagHeaderValue.Any) || (!tag.IsWeak && tag.Tag.Value == currentETag))
            ? null
            : ApiError.PreconditionFailed();
    }

    /// <summary>Checks whether the request has an <c>If-Match</c> header.</summary>
    /// <param name="request">The modifying request.</param>
    public static bool HasIfMatch(this HttpRequest request) => !string.IsNullOrWhiteSpace(request.Headers.IfMatch.ToString());
}
