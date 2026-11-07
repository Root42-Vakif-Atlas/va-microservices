using System.Net.Http.Headers;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Features;
using Scalar.AspNetCore;
using VakifAtlas.Gateway;
using VakifAtlas.ServiceDefaults;
using VakifAtlas.ServiceDefaults.Http;
using VakifAtlas.SharedKernel;
using Yarp.ReverseProxy.Transforms;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults("gateway");

builder.Services.AddReverseProxy()
    .LoadFromMemory(GatewayRoutes.Routes(!builder.Environment.IsProduction()), GatewayRoutes.Clusters(builder.Configuration, !builder.Environment.IsProduction()))
    .AddTransforms(context => context.AddRequestTransform(transform =>
    {
        HttpRequestHeaders headers = transform.ProxyRequest.Headers;

        // Strip internal headers so external callers cannot spoof them.
        foreach (var name in headers.Select(h => h.Key).Where(k => k.StartsWith("X-Internal-", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            headers.Remove(name);
        }

        if (transform.HttpContext.Items[CorrelationIdMiddleware.ItemKey] is string correlationId)
        {
            headers.Remove(CorrelationIdMiddleware.Header);
            headers.TryAddWithoutValidation(CorrelationIdMiddleware.Header, correlationId);
        }

        return ValueTask.CompletedTask;
    }));

IConfigurationSection limits = builder.Configuration.GetSection("Gateway:RateLimits");
builder.Services.AddRateLimiter(options =>
{
    // Coarse per IP protection, counted per replica. Business rule limits live in the services and are backed by Redis.
    options.AddPolicy(GatewayRoutes.DefaultRateLimitPolicy, context => RateLimitPartition.GetTokenBucketLimiter(
        ClientAddress(context), _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = limits.GetValue("DefaultBurst", 120),
            TokensPerPeriod = limits.GetValue("DefaultPerSecond", 20),
            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
            QueueLimit = 0,
        }));
    options.AddPolicy(GatewayRoutes.AuthRateLimitPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
        ClientAddress(context), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = limits.GetValue("AuthPerMinute", 30),
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
    options.OnRejected = async (context, cancellationToken) =>
    {
        var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan wait) ? (int)Math.Ceiling(wait.TotalSeconds) : 60;
        await ApiError.TooManyRequests(retryAfter).ToHttpResult().ExecuteAsync(context.HttpContext);
    };
});

string[] origins = builder.Configuration.GetSection("Gateway:Cors:Origins").Get<string[]>() ?? [];
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
{
    // Needed for the Flutter web client. Native mobile apps are not subject to CORS.
    if (origins.Contains("*"))
    {
        policy.AllowAnyOrigin();
    }
    else
    {
        policy.WithOrigins(origins);
    }

    policy.AllowAnyHeader()
        .WithMethods("GET", "HEAD", "POST", "PUT", "DELETE", "OPTIONS")
        // ETag: the web admin client reads it for If-Match (optimistic concurrency); without it every detail
        // load fails in the browser.
        .WithExposedHeaders(CorrelationIdMiddleware.Header, "Retry-After", "api-supported-versions",
            "api-deprecated-versions", "Deprecation", "Sunset", "ETag")
        .SetPreflightMaxAge(TimeSpan.FromHours(1));
}));

builder.Services.AddHsts(options => options.MaxAge = TimeSpan.FromDays(365));

WebApplication app = builder.Build();

// Kestrel's body limit is global (Http:MaxRequestBodyBytes); the VGM import alone needs more. Set before anything
// reads the body.
app.Use(async (context, next) =>
{
    if (GatewayRoutes.BodyLimitFor(context.Request.Path) is { } limit &&
        context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } body)
    {
        body.MaxRequestBodySize = limit;
    }

    await next(context);
});

app.UseServiceDefaults();
// Staging is also served on a public HTTPS domain, so HSTS is disabled only for local development.
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        IHeaderDictionary headers = context.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers.XFrameOptions = "DENY";
        return Task.CompletedTask;
    });
    await next(context);
});

app.UseCors();
app.UseRateLimiter();

// Lightweight liveness probe used by the client offline indicator via HEAD. It is exposed on both /health and
// /v1/health because the client resolves it relative to its configured base URL.
foreach (var path in new[] { "/health", $"/{GatewayRoutes.CurrentVersion}/health" })
{
    app.MapMethods(path, ["GET", "HEAD"], () => TypedResults.Ok(new { status = "ok" }))
        .DisableRateLimiting();
}

app.MapDefaultEndpoints(scalar =>
{
    // One reference for the whole API: each service's document is proxied under /docs/{service}/openapi.
    foreach (var service in GatewayRoutes.Prefixes.Values.Distinct().Append(GatewayRoutes.ModerationCluster))
    {
        scalar.AddDocument(service, service, $"/docs/{service}/openapi/v1.json", isDefault: service == "identity");
    }
});
app.MapReverseProxy();

app.Run();

static string ClientAddress(HttpContext context) =>
    context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

/// <summary>
/// Entry point type exposed so integration tests can host the gateway with <c>WebApplicationFactory&lt;Program&gt;</c>.
/// </summary>
public partial class Program
{
    /// <summary>Prevents direct instantiation while keeping the type available to the test host.</summary>
    protected Program()
    {
    }
}
