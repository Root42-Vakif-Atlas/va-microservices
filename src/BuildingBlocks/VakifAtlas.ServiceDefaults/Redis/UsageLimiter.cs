using StackExchange.Redis;
using VakifAtlas.ServiceDefaults.Observability;

namespace VakifAtlas.ServiceDefaults.Redis;

/// <summary>Result of a rate limit check.</summary>
/// <param name="Allowed">Whether the request is allowed.</param>
/// <param name="RetryAfterSeconds">Seconds to wait when rejected.</param>
public readonly record struct UsageDecision(bool Allowed, int RetryAfterSeconds)
{
    /// <summary>Allows the request.</summary>
    public static readonly UsageDecision Allow = new(true, 0);
}

/// <summary>Business rate limits shared across replicas through Redis.</summary>
public interface IUsageLimiter
{
    /// <summary>Counts a use; rejects once <paramref name="limit"/> uses fall within <paramref name="window"/>.</summary>
    /// <param name="key">Limit key, such as <c>staff-requests:{userId}</c>.</param>
    /// <param name="limit">Maximum uses per window.</param>
    /// <param name="window">Sliding window length.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<UsageDecision> TryConsumeAsync(string key, int limit, TimeSpan window, CancellationToken cancellationToken);

    /// <summary>Starts a cooldown; rejects while one is active.</summary>
    /// <param name="key">Cooldown key.</param>
    /// <param name="cooldown">Cooldown length.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<UsageDecision> TryStartCooldownAsync(string key, TimeSpan cooldown, CancellationToken cancellationToken);

    /// <summary>Clears the limit or cooldown for <paramref name="key"/>.</summary>
    /// <param name="key">Key to clear.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ResetAsync(string key, CancellationToken cancellationToken);
}

internal sealed class RedisUsageLimiter(IConnectionMultiplexer redis, TimeProvider clock) : IUsageLimiter
{
    // KEYS[1] = key; ARGV = now ms, window ms, limit, member
    private const string SlidingWindowScript = """
        redis.call('ZREMRANGEBYSCORE', KEYS[1], 0, tonumber(ARGV[1]) - tonumber(ARGV[2]))
        local count = redis.call('ZCARD', KEYS[1])
        if count < tonumber(ARGV[3]) then
          redis.call('ZADD', KEYS[1], ARGV[1], ARGV[4])
          redis.call('PEXPIRE', KEYS[1], ARGV[2])
          return 0
        end
        local oldest = redis.call('ZRANGE', KEYS[1], 0, 0, 'WITHSCORES')
        return tonumber(oldest[2]) + tonumber(ARGV[2]) - tonumber(ARGV[1])
        """;

    public async Task<UsageDecision> TryConsumeAsync(string key, int limit, TimeSpan window, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        var waitMs = (long)await redis.GetDatabase().ScriptEvaluateAsync(
            SlidingWindowScript,
            [Key(key)],
            [now, (long)window.TotalMilliseconds, limit, $"{now}:{Guid.NewGuid():N}"]);
        return Decide(key, waitMs);
    }

    public async Task<UsageDecision> TryStartCooldownAsync(string key, TimeSpan cooldown, CancellationToken cancellationToken)
    {
        IDatabase database = redis.GetDatabase();
        if (await database.StringSetAsync(Key(key), 1, cooldown, When.NotExists))
        {
            return UsageDecision.Allow;
        }

        TimeSpan remaining = await database.KeyTimeToLiveAsync(Key(key)) ?? cooldown;
        return Decide(key, (long)remaining.TotalMilliseconds);
    }

    public Task ResetAsync(string key, CancellationToken cancellationToken) => redis.GetDatabase().KeyDeleteAsync(Key(key));

    private static UsageDecision Decide(string key, long waitMs)
    {
        if (waitMs <= 0)
        {
            return UsageDecision.Allow;
        }

        Telemetry.UsageLimitRejections.Add(1, new KeyValuePair<string, object?>("limit", key.Split(':')[0]));
        return new UsageDecision(false, (int)Math.Ceiling(waitMs / 1000d));
    }

    private static string Key(string key) => $"usage:{key}";
}
