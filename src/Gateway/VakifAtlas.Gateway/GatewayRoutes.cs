using System.Text.RegularExpressions;
using Yarp.ReverseProxy.Configuration;

namespace VakifAtlas.Gateway;

/// <summary>
/// Builds the YARP route and cluster configuration that maps the first path segment to a backend service.
/// </summary>
/// <remarks>
/// Versioned paths such as <c>/v1/catalog/...</c> are forwarded unchanged. Legacy unversioned paths such as
/// <c>/catalog/...</c> are rewritten to <see cref="CurrentVersion"/> and answered with a <c>Deprecation</c> header,
/// so clients can migrate by appending <c>/v1/</c> to their <c>API_BASE_URL</c>.
/// </remarks>
public static partial class GatewayRoutes
{
    /// <summary>Name of the strict per IP rate limit policy applied to sign in and token endpoints under <c>/auth</c>.</summary>
    public const string AuthRateLimitPolicy = "auth-per-ip";

    /// <summary>Name of the general per IP rate limit policy applied to every other proxied route.</summary>
    public const string DefaultRateLimitPolicy = "per-ip";

    /// <summary>API version segment that legacy unversioned paths are rewritten to.</summary>
    public const string CurrentVersion = "v1";

    /// <summary>
    /// Body limit of the VGM bulk import (<c>catalog/admin/imports/**</c>, ADR 59): the data file with its image
    /// hashes is a few MB and an image up to 5 MB. Same value as the catalog endpoint's own limit; every other
    /// route keeps <c>Http:MaxRequestBodyBytes</c>.
    /// </summary>
    public const long ImportBodyBytes = 32L * 1024 * 1024;

    /// <summary>The larger body limit for <paramref name="path"/>, or null for the default.</summary>
    /// <param name="path">Request path, versioned (<c>/v1/catalog/…</c>) or legacy (<c>/catalog/…</c>).</param>
    public static long? BodyLimitFor(PathString path) =>
        ImportPath().IsMatch(path.Value ?? string.Empty) ? ImportBodyBytes : null;

    [GeneratedRegex(@"^/(v\d+/)?catalog/admin/imports(/|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ImportPath();


    /// <summary>Maps each public path prefix to the YARP cluster ID of the service that owns it.</summary>
    public static readonly IReadOnlyDictionary<string, string> Prefixes = new Dictionary<string, string>
    {
        ["auth"] = "identity",
        ["legal"] = "identity",
        ["catalog"] = "catalog",
        ["routes"] = "routes",
        ["saved-monuments"] = "routes",
        ["memories"] = "memories",
        ["civil-protection"] = "civil-protection",
        ["assistant"] = "assistant",
        ["gamification"] = "gamification",
        ["media"] = "media",
        ["route-exports"] = "media",
        ["feedback"] = "feedback",
    };

    /// <summary>
    /// Creates a versioned and a legacy route for every entry in <see cref="Prefixes"/>, plus a dedicated
    /// route for the Identity admin endpoints.
    /// </summary>
    /// <returns>The route definitions to load into the reverse proxy.</returns>
    /// <param name="includeDocs">Adds <c>/docs/{service}/openapi/...</c> routes that expose each service's OpenAPI document.</param>
    public static IReadOnlyList<RouteConfig> Routes(bool includeDocs = false)
    {
        var routes = new List<RouteConfig>();
        if (includeDocs)
        {
            foreach (var cluster in Prefixes.Values.Distinct())
            {
                routes.Add(new RouteConfig
                {
                    RouteId = $"{cluster}-openapi",
                    ClusterId = cluster,
                    RateLimiterPolicy = DefaultRateLimitPolicy,
                    Match = new RouteMatch { Path = $"/docs/{cluster}/openapi/{{**rest}}" },
                    Transforms = [new Dictionary<string, string> { ["PathRemovePrefix"] = $"/docs/{cluster}" }],
                });
            }

            // Moderation is internal only: just its FastAPI schema is exposed, never its API.
            routes.Add(new RouteConfig
            {
                RouteId = $"{ModerationCluster}-openapi",
                ClusterId = ModerationCluster,
                RateLimiterPolicy = DefaultRateLimitPolicy,
                Match = new RouteMatch { Path = $"/docs/{ModerationCluster}/openapi/v1.json" },
                Transforms = [new Dictionary<string, string> { ["PathPattern"] = "/openapi.json" }],
            });
        }

        foreach (var (prefix, cluster) in Prefixes)
        {
            var policy = prefix == "auth" ? AuthRateLimitPolicy : DefaultRateLimitPolicy;
            routes.Add(new RouteConfig
            {
                RouteId = $"{prefix}-versioned",
                ClusterId = cluster,
                RateLimiterPolicy = policy,
                Match = new RouteMatch { Path = $"/v{{version:regex(^\\d+$)}}/{prefix}/{{**rest}}" },
            });
            routes.Add(new RouteConfig
            {
                RouteId = $"{prefix}-legacy",
                ClusterId = cluster,
                RateLimiterPolicy = policy,
                Order = 10,
                Match = new RouteMatch { Path = $"/{prefix}/{{**rest}}" },
                Metadata = new Dictionary<string, string> { [LegacyMetadataKey] = "true" },
                Transforms =
                [
                    new Dictionary<string, string> { ["PathPrefix"] = $"/{CurrentVersion}" },
                    new Dictionary<string, string>
                    {
                        ["ResponseHeader"] = "Deprecation",
                        ["Set"] = "true",
                        ["When"] = "Always",
                    },
                ],
            });
        }

        // Staff management and the audit log live in Identity but are not sign in endpoints, so the admin
        // panel must not hit the strict auth limit while navigating. Per staff limits are enforced in the service.
        routes.Add(new RouteConfig
        {
            RouteId = "auth-admin-versioned",
            ClusterId = Prefixes["auth"],
            RateLimiterPolicy = DefaultRateLimitPolicy,
            Match = new RouteMatch { Path = "/v{version:regex(^\\d+$)}/auth/admin/{**rest}" },
        });

        return routes;
    }

    /// <summary>Cluster of the internal Python moderation service, routed only for its OpenAPI document.</summary>
    public const string ModerationCluster = "moderation";

    /// <summary>Route metadata key that marks a route as a legacy unversioned path.</summary>
    public const string LegacyMetadataKey = "legacy-unversioned";


    /// <summary>
    /// Creates one cluster per distinct service in <see cref="Prefixes"/> with active health checks against
    /// <c>/health/ready</c>.
    /// </summary>
    /// <param name="configuration">
    /// Configuration read for <c>Gateway:Services:{cluster}</c> destination addresses. When a key is missing the
    /// in cluster default <c>http://{cluster}:8080/</c> is used.
    /// </param>
    /// <returns>The cluster definitions to load into the reverse proxy.</returns>
    public static IReadOnlyList<ClusterConfig> Clusters(IConfiguration configuration, bool includeDocs = false) =>
        Prefixes.Values.Distinct().Concat(includeDocs ? [ModerationCluster] : []).Select(cluster => new ClusterConfig
        {
            ClusterId = cluster,
            HttpRequest = new Yarp.ReverseProxy.Forwarder.ForwarderRequestConfig
            {
                ActivityTimeout = TimeSpan.FromSeconds(30),
            },
            HealthCheck = new HealthCheckConfig
            {
                Active = new ActiveHealthCheckConfig
                {
                    Enabled = true,
                    Interval = TimeSpan.FromSeconds(15),
                    Timeout = TimeSpan.FromSeconds(5),
                    Path = "/health/ready",
                    Policy = "ConsecutiveFailures",
                },
            },
            Destinations = new Dictionary<string, DestinationConfig>
            {
                ["primary"] = new()
                {
                    Address = configuration[$"Gateway:Services:{cluster}"] ?? $"http://{cluster}:8080/",
                },
            },
        }).ToArray();
}
