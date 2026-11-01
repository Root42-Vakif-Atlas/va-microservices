using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using VakifAtlas.ServiceDefaults.Auth;
using VakifAtlas.ServiceDefaults.Http;
using VakifAtlas.SharedKernel;

namespace VakifAtlas.ServiceDefaults.Admin;

/// <summary>Replays the first response for a repeated <c>Idempotency-Key</c> for 24 hours.</summary>
public static class Idempotency
{
    /// <summary>Idempotency key header name.</summary>
    public const string Header = "Idempotency-Key";

    /// <summary>Maximum key length.</summary>
    public const int MaxKeyLength = 64;

    /// <summary>How long responses are kept for replay.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    private static readonly TimeSpan LockLifetime = TimeSpan.FromSeconds(30);

    /// <summary>Makes the endpoint safe to retry.</summary>
    /// <typeparam name="TBody">Request body type, used to detect a reused key.</typeparam>
    /// <param name="builder">The endpoint to protect.</param>
    public static RouteHandlerBuilder WithIdempotency<TBody>(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            HttpContext http = context.HttpContext;
            var key = http.Request.Headers[Header].ToString();
            IConnectionMultiplexer? redis = http.RequestServices.GetService<IConnectionMultiplexer>();
            if (string.IsNullOrEmpty(key) || redis is null)
            {
                return await next(context);
            }

            if (key.Length > MaxKeyLength || key.Any(c => c < 0x21 || c > 0x7e))
            {
                return ApiError.BadRequest().ToHttpResult();
            }

            JsonSerializerOptions json = http.RequestServices.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
            TBody? body = context.Arguments.OfType<TBody>().FirstOrDefault();
            var fingerprint = Convert.ToHexStringLower(SHA256.HashData(
                Encoding.UTF8.GetBytes($"{http.Request.Method} {http.Request.Path}\n{JsonSerializer.Serialize(body, json)}")));
            var storeKey = $"idempotency:{http.User.UserId() ?? "anonymous"}:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))}";
            IDatabase database = redis.GetDatabase();

            RedisValue stored = await database.StringGetAsync(storeKey);
            if (stored.HasValue)
            {
                return Replay(stored.ToString(), fingerprint);
            }

            // The lock rejects a parallel retry while the first request runs.
            var lockKey = $"{storeKey}:lock";
            if (!await database.StringSetAsync(lockKey, fingerprint, LockLifetime, When.NotExists))
            {
                return ApiError.Conflict(ErrorCodes.IdempotencyInProgress).ToHttpResult();
            }

            try
            {
                var result = await next(context);
                if (result is not IResult httpResult)
                {
                    return result;
                }

                // Buffer the response so it can be stored and sent.
                Stream original = http.Response.Body;
                await using var buffer = new MemoryStream();
                http.Response.Body = buffer;
                try
                {
                    await httpResult.ExecuteAsync(http);
                }
                finally
                {
                    http.Response.Body = original;
                }

                var status = http.Response.StatusCode;
                var bytes = buffer.ToArray();
                if (status is >= 200 and < 300)
                {
                    var record = new StoredResponse(fingerprint, status, http.Response.ContentType,
                        http.Response.Headers.ETag.ToString(), http.Response.Headers.Location.ToString(),
                        Convert.ToBase64String(bytes));
                    await database.StringSetAsync(storeKey, JsonSerializer.Serialize(record), Retention);
                }

                await original.WriteAsync(bytes, http.RequestAborted);
                return Results.Empty;
            }
            finally
            {
                await database.KeyDeleteAsync(lockKey);
            }
        });

    private static IResult Replay(string stored, string fingerprint)
    {
        StoredResponse record = JsonSerializer.Deserialize<StoredResponse>(stored)!;
        if (record.Fingerprint != fingerprint)
        {
            return new ApiError(422, ErrorCodes.IdempotencyKeyReused).ToHttpResult();
        }

        return new ReplayResult(record);
    }

    private sealed record StoredResponse(string Fingerprint, int Status, string? ContentType, string? ETag, string? Location,
        string Body);

    private sealed class ReplayResult(StoredResponse record) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = record.Status;
            httpContext.Response.ContentType = record.ContentType;
            if (!string.IsNullOrEmpty(record.ETag))
            {
                httpContext.Response.Headers.ETag = record.ETag;
            }

            if (!string.IsNullOrEmpty(record.Location))
            {
                httpContext.Response.Headers.Location = record.Location;
            }

            httpContext.Response.Headers["Idempotent-Replayed"] = "true";
            await httpContext.Response.Body.WriteAsync(Convert.FromBase64String(record.Body));
        }
    }
}
