using Asp.Versioning;
using Asp.Versioning.Builder;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace VakifAtlas.ServiceDefaults.Versioning;

/// <summary>URL segment API versioning, such as <c>/v1/catalog</c>.</summary>
public static class ApiVersioningExtensions
{
    /// <summary>API version 1.0.</summary>
    public static readonly ApiVersion V1 = new(1, 0);

    /// <summary>Path segment of the current API version, such as <c>v1</c>; the single source for URLs built in code.</summary>
    public static string CurrentSegment => $"v{V1.MajorVersion}";

    /// <summary>Registers versioning from the URL segment or the <c>api-version</c> header.</summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddVakifAtlasApiVersioning(this IServiceCollection services)
    {
        services
            .AddApiVersioning(options =>
            {
                options.DefaultApiVersion = V1;
                options.AssumeDefaultVersionWhenUnspecified = false;
                options.ReportApiVersions = true;
                options.ApiVersionReader = ApiVersionReader.Combine(
                    new UrlSegmentApiVersionReader(),
                    new HeaderApiVersionReader("api-version"));
            })
            .AddApiExplorer(options =>
            {
                options.GroupNameFormat = "'v'V";
                options.SubstituteApiVersionInUrl = true;
            })
            .AddOpenApi();
        return services;
    }

    /// <summary>Maps a <c>v{version}/&lt;prefix&gt;</c> route group.</summary>
    /// <param name="app">The endpoint route builder.</param>
    /// <param name="prefix">Path after the version, such as <c>catalog</c>.</param>
    public static RouteGroupBuilder MapVersionedGroup(this IEndpointRouteBuilder app, string prefix)
    {
        ApiVersionSet versions = app.NewApiVersionSet()
            .HasApiVersion(V1)
            .ReportApiVersions()
            .Build();
        return app.MapGroup($"v{{version:apiVersion}}/{prefix}").WithApiVersionSet(versions);
    }
}
