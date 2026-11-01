using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using VakifAtlas.SharedKernel;

namespace VakifAtlas.CatalogClient;

/// <summary>Geographic coordinates of a monument.</summary>
/// <param name="Latitude">Latitude in decimal degrees.</param>
/// <param name="Longitude">Longitude in decimal degrees.</param>
public sealed record CatalogLocation(double Latitude, double Longitude);

/// <summary>Lightweight monument record from the <c>view=location</c> projection, localized to the request language.</summary>
/// <param name="Id">Monument identifier.</param>
/// <param name="Name">Localized monument name.</param>
/// <param name="Category">Localized monument category.</param>
/// <param name="City">City the monument is in.</param>
/// <param name="District">District the monument is in.</param>
/// <param name="Location">Coordinates of the monument, or null when none have been entered.</param>
public sealed record CatalogMonument(string Id, string Name, string Category, string City, string District, CatalogLocation? Location)
{
    /// <summary>Monument location as a <see cref="GeoPoint"/>, or null when the monument has no location.</summary>
    public GeoPoint? Point => Location is null ? null : new GeoPoint(Location.Latitude, Location.Longitude);
}

/// <summary>Reads monuments from the catalog service, the only synchronous dependency between services.</summary>
public interface ICatalogClient
{
    /// <summary>Resolves monuments by id in batches of 50, caching each one per language in memory and Redis. Unknown ids are left out of the result.</summary>
    /// <param name="ids">Monument ids to resolve.</param>
    /// <param name="language">Language used for localized fields and the cache key.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    Task<IReadOnlyDictionary<string, CatalogMonument>> GetAsync(IEnumerable<string> ids, string language,
        CancellationToken cancellationToken);
}

internal sealed class HttpCatalogClient(HttpClient http, HybridCache cache) : ICatalogClient
{
    private const int BatchSize = 50;

    private static readonly HybridCacheEntryOptions Entry = new()
    {
        Expiration = TimeSpan.FromMinutes(10),
        LocalCacheExpiration = TimeSpan.FromMinutes(2),
    };

    public async Task<IReadOnlyDictionary<string, CatalogMonument>> GetAsync(IEnumerable<string> ids, string language,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, CatalogMonument>(StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var id in ids.Distinct(StringComparer.Ordinal))
        {
            CatalogMonument? cached = await cache.GetOrCreateAsync<CatalogMonument?>(Key(language, id), _ => ValueTask.FromResult<CatalogMonument?>(null),
                new HybridCacheEntryOptions { Flags = HybridCacheEntryFlags.DisableUnderlyingData | HybridCacheEntryFlags.DisableLocalCacheWrite | HybridCacheEntryFlags.DisableDistributedCacheWrite },
                cancellationToken: cancellationToken);
            if (cached is null)
            {
                missing.Add(id);
            }
            else
            {
                result[id] = cached;
            }
        }

        // Monuments are catalog works; the id is the work key (`works?keys=` accepts at most 50).
        foreach (var batch in missing.Chunk(BatchSize))
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"catalog/works?view=location&limit={batch.Length}&keys={Uri.EscapeDataString(string.Join(',', batch))}");
            request.Headers.AcceptLanguage.ParseAdd(language);
            using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            LocationPage page = await response.Content.ReadFromJsonAsync<LocationPage>(cancellationToken)
                ?? throw new InvalidOperationException("Empty catalog response.");
            foreach (WorkLocation work in page.Items)
            {
                var monument = new CatalogMonument(work.Key, work.Title, work.Category, work.City.Name, work.District ?? string.Empty,
                    work.Location);
                result[monument.Id] = monument;
                await cache.SetAsync(Key(language, monument.Id), monument, Entry, cancellationToken: cancellationToken);
            }
        }

        return result;
    }

    private static string Key(string language, string id) => $"catalog:monument:{language}:{id}";

    private sealed record LocationPage(List<WorkLocation> Items);

    private sealed record WorkCity(short Code, string Name);

    private sealed record WorkLocation(string Key, string Title, string Category, WorkCity City, string? District,
        CatalogLocation? Location);
}

/// <summary>Registers the catalog client.</summary>
public static class CatalogClientExtensions
{
    /// <summary>Adds <see cref="ICatalogClient"/> with a hybrid cache backed by Redis when configured, and a resilient HTTP client.</summary>
    /// <param name="builder">The application builder.</param>
    public static WebApplicationBuilder AddCatalogClient(this WebApplicationBuilder builder)
    {
        var redis = builder.Configuration.GetConnectionString("Redis");
        if (!string.IsNullOrEmpty(redis))
        {
            builder.Services.AddStackExchangeRedisCache(options =>
            {
                options.Configuration = redis;
                options.InstanceName = $"{builder.Environment.ApplicationName}:";
            });
        }

        builder.Services.AddHybridCache();
        builder.Services
            .AddHttpClient<ICatalogClient, HttpCatalogClient>(http =>
                http.BaseAddress = new Uri(builder.Configuration["Services:Catalog"] ?? "http://catalog:8080/v1/"))
            .AddStandardResilienceHandler();
        return builder;
    }
}
