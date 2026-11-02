using System.Globalization;
using System.Net;
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

/// <summary>
/// Durak çiftleri arası karayolu süresi (sn) ve mesafesi (m): <c>[i][j]</c> = <c>i</c> → <c>j</c>. Yol yoksa
/// <c>null</c>.
/// </summary>
public sealed record TravelMatrix(int?[][] DurationSeconds, int?[][] DistanceMeters);

/// <summary>İki eser arasındaki karayolu bacağı ve durakların süre/mesafe matrisi (<c>travelMode: driving</c>).</summary>
public interface IRoutingEngine
{
    string Name { get; }

    Task<RouteLeg> LegAsync(CatalogMonument from, CatalogMonument to, CancellationToken cancellationToken);

    Task<TravelMatrix> MatrixAsync(IReadOnlyList<CatalogMonument> stops, CancellationToken cancellationToken);
}

/// <summary>
/// Kurum içi OSRM (çalışma anında üçüncü taraf yönlendirme servisine çıkılmaz, ADR 22). Geometri tam
/// çözünürlüktedir (<c>overview=full</c>): sadeleştirilmiş çizgi yakın zum'da yoldan sapıyordu.
/// </summary>
internal sealed class OsrmRoutingEngine(HttpClient http, IOptions<RoutingOptions> options) : IRoutingEngine
{
    /// <summary>Geometri biçimi değişince artırılır: önbellekteki eski bacaklar yeniden hesaplanır.</summary>
    private const string GeometryRevision = "full";

    public string Name => $"osrm:{options.Value.OsrmDataVersion}:{GeometryRevision}";

    public async Task<RouteLeg> LegAsync(CatalogMonument from, CatalogMonument to, CancellationToken cancellationToken)
    {
        var (a, b) = (RoutingStops.Located(from), RoutingStops.Located(to));
        var path = string.Create(CultureInfo.InvariantCulture,
            $"route/v1/driving/{a.Longitude},{a.Latitude};{b.Longitude},{b.Latitude}?overview=full&geometries=polyline&steps=false&alternatives=false");
        using var response = await http.GetAsync(path, cancellationToken);
        // OSRM yol bulamadığında da gövde döner (400 NoRoute / NoSegment); diğer hatalar gerçek arızadır.
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.BadRequest)
        {
            response.EnsureSuccessStatusCode();
        }

        var body = await response.Content.ReadFromJsonAsync<OsrmResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Empty OSRM response.");
        return body switch
        {
            { Code: "Ok", Routes: [var route, ..] } =>
                new RouteLeg((int)Math.Round(route.Distance), (int)Math.Round(route.Duration), route.Geometry),
            { Code: "NoRoute" or "NoSegment" } => throw new RouteNotFoundException(from.Id, to.Id, body.Code),
            _ => throw new InvalidOperationException($"OSRM returned {body.Code}."),
        };
    }

    /// <summary>OSRM <c>table</c> servisi: tüm çiftler tek istekte (en çok 10 durak).</summary>
    public async Task<TravelMatrix> MatrixAsync(IReadOnlyList<CatalogMonument> stops, CancellationToken cancellationToken)
    {
        var coordinates = string.Join(';', stops.Select(RoutingStops.Located)
            .Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.Longitude},{p.Latitude}")));
        var body = await http.GetFromJsonAsync<OsrmTable>($"table/v1/driving/{coordinates}?annotations=duration,distance",
                cancellationToken)
            ?? throw new InvalidOperationException("Empty OSRM response.");
        if (body.Code != "Ok" || body.Durations is null || body.Distances is null)
        {
            throw new InvalidOperationException($"OSRM returned {body.Code}.");
        }

        static int?[][] Round(double?[][] values) =>
            [.. values.Select(row => row.Select(v => v is { } value ? (int?)Math.Round(value) : null).ToArray())];
        return new TravelMatrix(Round(body.Durations), Round(body.Distances));
    }

    private sealed record OsrmResponse(string Code, List<OsrmRoute>? Routes);

    private sealed record OsrmRoute(double Distance, double Duration, string Geometry);

    private sealed record OsrmTable(string Code, double?[][]? Durations, double?[][]? Distances);
}

/// <summary>Karayoluyla bağlantı yok (ada, yola uzak konum); bacak tahmine düşer.</summary>
public sealed class RouteNotFoundException(string fromId, string toId, string code)
    : Exception($"No road route between {fromId} and {toId} ({code}).");

/// <summary>
/// Dev/test: Flutter mock'unun OSRM'den bir kez alınmış eser çiftleri (ters yön polyline ters
/// çevrilir). Bilinmeyen çiftte <see cref="RouteEstimate"/> (düz çizgi).
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

        return Task.FromResult(RouteEstimate.Straight(from, to));
    }

    public async Task<TravelMatrix> MatrixAsync(IReadOnlyList<CatalogMonument> stops, CancellationToken cancellationToken)
    {
        var durations = new int?[stops.Count][];
        var distances = new int?[stops.Count][];
        for (var i = 0; i < stops.Count; i++)
        {
            (durations[i], distances[i]) = (new int?[stops.Count], new int?[stops.Count]);
            for (var j = 0; j < stops.Count; j++)
            {
                var leg = i == j ? new RouteLeg(0, 0, string.Empty) : await LegAsync(stops[i], stops[j], cancellationToken);
                (durations[i][j], distances[i][j]) = (leg.DurationSeconds, leg.DistanceMeters);
            }
        }

        return new TravelMatrix(durations, distances);
    }

    private sealed record LegFile(List<LegRecord> Legs);

    private sealed record LegRecord(string From, string To, int DistanceMeters, int DurationSeconds, string Geometry);
}

/// <summary>Yol bilinmediğinde: kuş uçuşu × 1,3 ve 70 km/sa (düz çizgi).</summary>
internal static class RouteEstimate
{
    public static RouteLeg Straight(CatalogMonument from, CatalogMonument to)
    {
        var (a, b) = (RoutingStops.Located(from), RoutingStops.Located(to));
        var distance = Geo.DistanceMeters(a, b) * 1.3;
        return new RouteLeg((int)distance, (int)(distance / (70_000d / 3600)),
            Polyline.Encode([(a.Latitude, a.Longitude), (b.Latitude, b.Longitude)]));
    }
}

internal static class RoutingStops
{
    /// <summary>Durak konumu; konumsuz eser rota doğrulamasında reddedilir (<c>monument_without_location</c>).</summary>
    public static GeoPoint Located(CatalogMonument monument) =>
        monument.Point ?? throw new InvalidOperationException($"Monument {monument.Id} has no location.");
}

/// <summary>
/// Bacakları hesaplar; eser çifti başına sonuç veritabanında önbelleklenir (motor + veri sürümü anahtarlı).
/// Her bacak eser konumlarına bağlanır (<see cref="RouteLeg.Anchored"/>); önbellekteki bacak eserlerin bugünkü
/// konumunu bağlamıyorsa (konum panelde düzeltildi) yeniden hesaplanır.
/// </summary>
public sealed class LegCalculator(IRoutingEngine engine, RoutesDbContext db, TimeProvider clock, ILogger<LegCalculator> logger)
{
    public string Engine => engine.Name;

    public async Task<IReadOnlyList<RouteLeg>> CalculateAsync(IReadOnlyList<CatalogMonument> stops, CancellationToken cancellationToken)
    {
        var pairs = stops.Zip(stops.Skip(1)).ToList();
        var fromIds = pairs.Select(p => p.First.Id).ToList();
        var cached = await db.Legs.AsNoTracking()
            .Where(l => l.Engine == engine.Name && fromIds.Contains(l.FromId))
            .ToListAsync(cancellationToken);

        var legs = new RouteLeg[pairs.Count];
        var computed = new bool[pairs.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, pairs.Count), cancellationToken, async (index, token) =>
        {
            var (from, to) = pairs[index];
            var (a, b) = (RoutingStops.Located(from), RoutingStops.Located(to));
            var hit = cached.Where(l => l.FromId == from.Id && l.ToId == to.Id)
                .Select(l => new RouteLeg(l.DistanceMeters, l.DurationSeconds, l.Geometry))
                .FirstOrDefault(l => l.Connects(a, b));
            if (hit is not null)
            {
                legs[index] = hit;
                return;
            }

            legs[index] = (await LegAsync(from, to, token)).Anchored(a, b);
            computed[index] = true;
        });

        // Eşzamanlı rotalar aynı çifti hesaplayabilir; konumu değişen eserin eski bacağı güncellenir.
        var now = clock.GetUtcNow();
        for (var index = 0; index < pairs.Count; index++)
        {
            if (!computed[index])
            {
                continue;
            }

            var ((from, to), leg) = (pairs[index], legs[index]);
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO leg_cache (from_id, to_id, engine, distance_meters, duration_seconds, geometry, created_at)
                VALUES ({from.Id}, {to.Id}, {engine.Name}, {leg.DistanceMeters}, {leg.DurationSeconds}, {leg.Geometry}, {now})
                ON CONFLICT (from_id, to_id, engine) DO UPDATE SET
                    distance_meters = excluded.distance_meters, duration_seconds = excluded.duration_seconds,
                    geometry = excluded.geometry, created_at = excluded.created_at
                """, cancellationToken);
        }

        return legs;
    }

    private async Task<RouteLeg> LegAsync(CatalogMonument from, CatalogMonument to, CancellationToken cancellationToken)
    {
        try
        {
            return await engine.LegAsync(from, to, cancellationToken);
        }
        catch (RouteNotFoundException exception)
        {
            // Veri sürümü aynı kaldıkça sonuç değişmez: tahmin de önbelleklenir.
            logger.LogWarning(exception, "Road route not found; using a straight-line estimate");
            return RouteEstimate.Straight(from, to);
        }
    }
}
