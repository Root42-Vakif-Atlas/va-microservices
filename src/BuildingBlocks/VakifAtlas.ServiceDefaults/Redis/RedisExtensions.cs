using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;
using VakifAtlas.ServiceDefaults.Auth;

namespace VakifAtlas.ServiceDefaults.Redis;

/// <summary>Redis-backed service registration.</summary>
public static class RedisExtensions
{
    /// <summary>Registers Redis from <c>ConnectionStrings:Redis</c> with the rate limiter and session revocation store.</summary>
    /// <param name="builder">The application builder.</param>
    public static WebApplicationBuilder AddRedis(this WebApplicationBuilder builder)
    {
        var connectionString = builder.Configuration.GetConnectionString("Redis")
            ?? throw new InvalidOperationException("ConnectionStrings:Redis is required.");
        var options = ConfigurationOptions.Parse(connectionString);
        options.AbortOnConnectFail = false;
        options.ClientName = builder.Environment.ApplicationName;

        builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(options));
        builder.Services.AddSingleton<IUsageLimiter, RedisUsageLimiter>();
        builder.Services.AddSingleton<ISessionRevocationStore, RedisSessionRevocationStore>();
        builder.Services.AddHealthChecks().AddCheck<RedisHealthCheck>("redis", HealthStatus.Degraded, tags: [ServiceDefaultsExtensions.ReadyTag]);
        return builder;
    }
}

internal sealed class RedisHealthCheck(IConnectionMultiplexer redis) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await redis.GetDatabase().PingAsync();
            return HealthCheckResult.Healthy();
        }
        catch (RedisException exception)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "Redis unreachable", exception);
        }
    }
}

internal sealed class RedisSessionRevocationStore(IConnectionMultiplexer redis) : ISessionRevocationStore
{
    /// <summary>Access token lifetime plus clock skew.</summary>
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(16);

    public async Task RevokeAsync(IEnumerable<string> sessionIds, CancellationToken cancellationToken)
    {
        IDatabase database = redis.GetDatabase();
        await Task.WhenAll(sessionIds.Select(id => database.StringSetAsync(Key(id), 1, Retention)));
    }

    public Task<bool> IsRevokedAsync(string sessionId, CancellationToken cancellationToken) =>
        redis.GetDatabase().KeyExistsAsync(Key(sessionId));

    private static string Key(string sessionId) => $"auth:revoked-sid:{sessionId}";
}
