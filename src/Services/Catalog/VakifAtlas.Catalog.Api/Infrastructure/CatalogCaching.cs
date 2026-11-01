using Microsoft.AspNetCore.OutputCaching;

namespace VakifAtlas.Catalog.Api.Infrastructure;

// the public catalog is anonymous, so responses are cached at the CDN and in the service (Redis output cache,
// shared by replicas). Key: path + query + language. Every write evicts Tag; tile URLs also carry the catalog
// version (v), so a CDN never mixes tiles of two versions
public static class CatalogCaching
{
    public const string Tag = "catalog";

    public const string ListPolicy = "catalog-list";
    public const string TilePolicy = "catalog-tile";
    public const string ExtentPolicy = "catalog-extent";

    public static readonly TimeSpan ListMaxAge = TimeSpan.FromHours(6);
    public static readonly TimeSpan TileMaxAge = TimeSpan.FromDays(1);
    public static readonly TimeSpan ExtentMaxAge = TimeSpan.FromMinutes(5);

    public static void AddPolicies(OutputCacheOptions options)
    {
        options.AddPolicy(ListPolicy, Policy(ListMaxAge));
        options.AddPolicy(TilePolicy, Policy(TileMaxAge));
        options.AddPolicy(ExtentPolicy, Policy(ExtentMaxAge));
    }

    private static Action<OutputCachePolicyBuilder> Policy(TimeSpan maxAge) => builder => builder
        .Expire(maxAge)
        .SetVaryByQuery("*")
        .SetVaryByHeader("Accept-Language")
        .Tag(Tag);

    // shared cache headers (CDN, client)
    public static TBuilder WithCacheHeaders<TBuilder>(this TBuilder builder, TimeSpan maxAge) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            var result = await next(context);
            var response = context.HttpContext.Response;
            response.OnStarting(() =>
            {
                if (response.StatusCode == StatusCodes.Status200OK)
                {
                    response.Headers.CacheControl = $"public, max-age={(int)maxAge.TotalSeconds}";
                }

                response.Headers.Vary = "Accept-Language";
                return Task.CompletedTask;
            });
            return result;
        });
}
