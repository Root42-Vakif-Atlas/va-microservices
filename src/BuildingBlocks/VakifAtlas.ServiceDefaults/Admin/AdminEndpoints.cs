using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using VakifAtlas.ServiceDefaults.Auth;
using VakifAtlas.ServiceDefaults.Http;
using VakifAtlas.ServiceDefaults.Redis;
using VakifAtlas.ServiceDefaults.Versioning;
using VakifAtlas.SharedKernel;

namespace VakifAtlas.ServiceDefaults.Admin;

/// <summary>Shared setup for VGM Admin Panel endpoints.</summary>
public static class AdminEndpoints
{
    /// <summary>Request limit per staff member per minute.</summary>
    public const int RequestsPerMinute = 600;

    /// <summary>Maps a staff route group with <paramref name="policy"/>, no caching and per-staff rate limiting.</summary>
    /// <param name="app">The endpoint route builder.</param>
    /// <param name="prefix">Path after the version, such as <c>catalog/admin</c>.</param>
    /// <param name="policy">A <see cref="StaffPolicies"/> policy name.</param>
    /// <param name="requestsPerMinute">Request limit per staff member per minute; defaults to <see cref="RequestsPerMinute"/>.</param>
    /// <param name="limitName">Name of the rate limit counter. Groups with the same name share one budget per staff member.</param>
    public static RouteGroupBuilder MapAdminGroup(this IEndpointRouteBuilder app, string prefix, string policy,
        int requestsPerMinute = RequestsPerMinute, string limitName = "staff-requests") =>
        app.MapVersionedGroup(prefix)
            .RequireAuthorization(policy)
            .AddEndpointFilter((context, next) => NoStoreAndRateLimit(context, next, requestsPerMinute, limitName));

    private static async ValueTask<object?> NoStoreAndRateLimit(EndpointFilterInvocationContext context,
        EndpointFilterDelegate next, int requestsPerMinute, string limitName)
    {
        HttpContext http = context.HttpContext;
        http.Response.Headers.CacheControl = "private, no-store";
        var userId = http.User.UserId();
        IUsageLimiter? limiter = http.RequestServices.GetService<IUsageLimiter>();
        if (limiter is not null && userId is not null)
        {
            UsageDecision decision = await limiter.TryConsumeAsync($"{limitName}:{userId}", requestsPerMinute,
                TimeSpan.FromMinutes(1), http.RequestAborted);
            if (!decision.Allowed)
            {
                return ApiError.TooManyRequests(decision.RetryAfterSeconds).ToHttpResult();
            }
        }

        return await next(context);
    }
}
