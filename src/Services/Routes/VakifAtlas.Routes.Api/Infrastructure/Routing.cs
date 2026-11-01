using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using VakifAtlas.CatalogClient;
using VakifAtlas.Routes.Domain;
using VakifAtlas.SharedKernel;

namespace VakifAtlas.Routes.Api.Infrastructure;

public sealed class RoutingOptions
{
    public const string Section = "Routing";

    /// <summary><c>osrm</c> (kurum içi OSRM, Türkiye extract) veya <c>fixture</c> (dev: seed/route-legs.json).</summary>
    public string Engine { get; set; } = "fixture";

    public string OsrmUrl { get; set; } = "http://osrm:5000/";

    /// <summary>OSRM veri sürümü (extract tarihi); değişince bacak önbelleği yeniden hesaplanır.</summary>
    public string OsrmDataVersion { get; set; } = "turkey-latest";

    public string FixturePath { get; set; } = "seed/route-legs.json";
}

/// <summary>İki eser arasındaki karayolu bacağı (<c>travelMode: driving</c>).</summary>
public interface IRoutingEngine
{
    string Name { get; }

    Task<RouteLeg> LegAsync(CatalogMonument from, CatalogMonument to, CancellationToken cancellationToken);
}

/// <summary>
/// Kurum içi OSRM (çalışma anında üçüncü taraf yönlendirme servisine çıkılmaz, ADR 22).
/// </summary>
internal sealed class OsrmRoutingEngine(HttpClient http, IOptions<RoutingOptions> options) : IRoutingEngine
{
    public string Name => $"osrm:{options.Value.OsrmDataVersion}";

    public async Task<RouteLeg> LegAsync(CatalogMonument from, CatalogMonument to, CancellationToken cancellationToken)
    {
        var (a, b) = (RoutingStops.Located(from), RoutingStops.Located(to));
        var path = string.Create(CultureInfo.InvariantCulture,
            $"route/v1/driving/{a.Longitude},{a.Latitude};{b.Longitude},{b.Latitude}?overview=simplified&geometries=polyline");
        var response = await http.GetFromJsonAsync<OsrmResponse>(path, cancellationToken)
            ?? throw new InvalidOperationException("Empty OSRM response.");
        var route = response.Code == "Ok" && response.Routes is [var first, ..]
            ? first
            : throw new InvalidOperationException($"OSRM returned {response.Code}.");
        return new RouteLeg((int)Math.Round(route.Distance), (int)Math.Round(route.Duration), route.Geometry);
    }

    private sealed record OsrmResponse(string Code, List<OsrmRoute>? Routes);

    private sealed record OsrmRoute(double Distance, double Duration, string Geometry);
}

/// <summary>
/// Dev/test: Flutter mock'unun OSRM'den bir kez alınmış 45 eser çifti (ters yön polyline ters
/// çevrilir). Bilinmeyen çiftte kuş uçuşu × 1,3 ve 70 km/sa tahmini (düz çizgi).
/// </summary>
internal sealed class FixtureRoutingEngine : IRoutingEngine
{
    private readonly Dictionary<string, RouteLeg> _legs;

    public FixtureRoutingEngine(IOptions<RoutingOptions> options, IHostEnvironment environment)
    {
        var path = new[] { environment.ContentRootPath, AppContext.BaseDirectory }
            .Select(root => Path.Combine(root, options.Value.FixturePath))
            .FirstOrDefault(File.Exists);
        _legs = path is null
            ? []
            : JsonSerializer.Deserialize<LegFile>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))!
                .Legs.ToDictionary(l => $"{l.From}|{l.To}", l => new RouteLeg(l.DistanceMeters, l.DurationSeconds, l.Geometry));
    }

    public string Name => "fixture";

    public Task<RouteLeg> LegAsync(CatalogMonument from, CatalogMonument to, CancellationToken cancellationToken)
    {
        if (_legs.TryGetValue($"{from.Id}|{to.Id}", out var forward))
        {
            return Task.FromResult(forward);
        }

        if (_legs.TryGetValue($"{to.Id}|{from.Id}", out var backward))
        {
            return Task.FromResult(backward with { Geometry = Polyline.Reverse(backward.Geometry) });
        }

        var (a, b) = (RoutingStops.Located(from), RoutingStops.Located(to));
        var distance = Geo.DistanceMeters(a, b) * 1.3;
        return Task.FromResult(new RouteLeg((int)distance, (int)(distance / (70_000d / 3600)),
            Polyline.Encode([(a.Latitude, a.Longitude), (b.Latitude, b.Longitude)])));
    }

    private sealed record LegFile(List<LegRecord> Legs);

    private sealed record LegRecord(string From, string To, int DistanceMeters, int DurationSeconds, string Geometry);
}

internal static class RoutingStops
{
    /// <summary>Durak konumu; konumsuz eser rota doğrulamasında reddedilir (<c>monument_without_location</c>).</summary>
    public static GeoPoint Located(CatalogMonument monument) =>
        monument.Point ?? throw new InvalidOperationException($"Monument {monument.Id} has no location.");
}

/// <summary>Bacakları hesaplar; eser çifti başına sonuç veritabanında önbelleklenir.</summary>
public sealed class LegCalculator(IRoutingEngine engine, RoutesDbContext db, TimeProvider clock)
{
    public async Task<IReadOnlyList<RouteLeg>> CalculateAsync(IReadOnlyList<CatalogMonument> stops, CancellationToken cancellationToken)
    {
        var pairs = stops.Zip(stops.Skip(1)).ToList();
        var fromIds = pairs.Select(p => p.First.Id).ToList();
        var cached = await db.Legs.AsNoTracking()
            .Where(l => l.Engine == engine.Name && fromIds.Contains(l.FromId))
            .ToListAsync(cancellationToken);

        var legs = new RouteLeg[pairs.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, pairs.Count), cancellationToken, async (index, token) =>
        {
            var (from, to) = pairs[index];
            var hit = cached.FirstOrDefault(l => l.FromId == from.Id && l.ToId == to.Id);
            legs[index] = hit is not null
                ? new RouteLeg(hit.DistanceMeters, hit.DurationSeconds, hit.Geometry)
                : await engine.LegAsync(from, to, token);
        });

        // Eşzamanlı rotalar aynı çifti hesaplayabilir: önbellek yazımı çakışmada sessizce atlanır.
        var now = clock.GetUtcNow();
        foreach (var (pair, leg) in pairs.Zip(legs).Where(p => !cached.Any(l => l.FromId == p.First.First.Id && l.ToId == p.First.Second.Id)))
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO leg_cache (from_id, to_id, engine, distance_meters, duration_seconds, geometry, created_at)
                VALUES ({pair.First.Id}, {pair.Second.Id}, {engine.Name}, {leg.DistanceMeters}, {leg.DurationSeconds}, {leg.Geometry}, {now})
                ON CONFLICT DO NOTHING
                """, cancellationToken);
        }

        return legs;
    }
}
