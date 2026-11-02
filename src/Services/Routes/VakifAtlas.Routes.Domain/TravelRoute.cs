using VakifAtlas.SharedKernel;

namespace VakifAtlas.Routes.Domain;

public static class TravelModes
{
    public const string Driving = "driving";
}

/// <summary>Mola türleri; parantezdekiler istemcinin önerdiği varsayılan süre.</summary>
public static class BreakKinds
{
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        "shortBreak", // 15 dk
        "meal",       // 45 dk
        "rest",       // 2 sa
        "overnight",  // 10 sa
    };
}

public sealed record RouteBreak(string Kind, int Minutes);

public sealed record RouteStopPlan(string MonumentId, int VisitMinutes, IReadOnlyList<RouteBreak> Breaks);

public sealed record RoutePlan(string Name, string TravelMode, IReadOnlyList<RouteStopPlan> Stops);

/// <summary>Durak <c>i</c> → <c>i+1</c> karayolu bacağı; <see cref="Geometry"/> Google encoded polyline (hassasiyet 5).</summary>
public sealed record RouteLeg(int DistanceMeters, int DurationSeconds, string Geometry)
{
    /// <summary>
    /// Çizgiyi iki eserin konumuna bağlar: yönlendirme motoru yolu en yakın karayolundan başlatır, eser
    /// yoldan uzaktaysa (avlu, park) haritada pin ile çizgi arasında boşluk kalırdı.
    /// </summary>
    public RouteLeg Anchored(GeoPoint from, GeoPoint to)
    {
        var points = Polyline.Decode(Geometry).ToList();
        if (points.Count == 0 || !Polyline.SamePoint(points[0], (from.Latitude, from.Longitude)))
        {
            points.Insert(0, (from.Latitude, from.Longitude));
        }

        if (!Polyline.SamePoint(points[^1], (to.Latitude, to.Longitude)))
        {
            points.Add((to.Latitude, to.Longitude));
        }

        return this with { Geometry = Polyline.Encode(points) };
    }

    /// <summary>Çizgi iki eserin bugünkü konumunda başlayıp bitiyor mu (eser konumu panelde düzeltilmiş olabilir).</summary>
    public bool Connects(GeoPoint from, GeoPoint to)
    {
        var points = Polyline.Decode(Geometry);
        return points.Count >= 2 &&
            Polyline.SamePoint(points[0], (from.Latitude, from.Longitude)) &&
            Polyline.SamePoint(points[^1], (to.Latitude, to.Longitude));
    }
}

public sealed record RouteSummary(int DistanceMeters, int TravelSeconds, int VisitSeconds, int BreakSeconds, int TotalSeconds)
{
    /// <summary>Toplam = yolculuk + ziyaret + mola (ADR 22).</summary>
    public static RouteSummary Compute(IReadOnlyList<RouteStop> stops, IReadOnlyList<RouteLeg> legs)
    {
        var distance = legs.Sum(l => l.DistanceMeters);
        var travel = legs.Sum(l => l.DurationSeconds);
        var visit = stops.Sum(s => s.VisitMinutes * 60);
        var breaks = stops.Sum(s => s.Breaks.Sum(b => b.Minutes) * 60);
        return new RouteSummary(distance, travel, visit, breaks, travel + visit + breaks);
    }
}

/// <summary>Kaydedilmiş durak (mola listesi JSON'da saklanır).</summary>
public sealed class RouteStop
{
    public required string MonumentId { get; init; }

    public int VisitMinutes { get; init; }

    public List<RouteBreak> Breaks { get; init; } = [];
}

/// <summary>
/// Cihaza bağlı gezi planı (ADR 21): <see cref="InstallationId"/> sahibidir; giriş gerekmez.
/// Oturum açıkken oluşturulduysa <see cref="UserId"/> yalnızca oyunlaştırma için tutulur.
/// </summary>
public sealed class TravelRoute
{
    public required string Id { get; init; }

    public Guid InstallationId { get; init; }

    public string? UserId { get; set; }

    public required string Name { get; set; }

    public string TravelMode { get; set; } = TravelModes.Driving;

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<RouteStop> Stops { get; set; } = [];

    public List<RouteLeg> Legs { get; set; } = [];

    /// <summary>Bacakları hesaplayan motor ve veri sürümü (<c>osrm:turkey-latest</c>); eski kayıtlarda boş.</summary>
    public string? LegsEngine { get; set; }

    /// <summary>Durak illeri (liste satırı; il adları dile bağlı değildir).</summary>
    public List<string> Cities { get; set; } = [];

    public RouteSummary Summary => RouteSummary.Compute(Stops, Legs);

    public void Apply(RoutePlan plan, IReadOnlyList<RouteLeg> legs, string legsEngine, IReadOnlyList<string> cities, DateTimeOffset now)
    {
        Name = plan.Name.Trim();
        TravelMode = plan.TravelMode;
        Stops = plan.Stops.Select(s => new RouteStop { MonumentId = s.MonumentId, VisitMinutes = s.VisitMinutes, Breaks = [.. s.Breaks] }).ToList();
        Legs = [.. legs];
        LegsEngine = legsEngine;
        Cities = [.. cities];
        UpdatedAt = now;
    }

    /// <summary>
    /// Bacaklar bu motorla ve duraklar bugünkü konumundayken hesaplanmış mı. Değilse (motor/veri sürümü
    /// değişti, eser taşındı) yeniden hesaplanır; plan değişmediğinden <see cref="UpdatedAt"/> korunur.
    /// </summary>
    public bool LegsAreCurrent(IReadOnlyList<GeoPoint> stops, string engine) =>
        LegsEngine == engine && Legs.Count == stops.Count - 1 &&
        Legs.Select((leg, index) => leg.Connects(stops[index], stops[index + 1])).All(connected => connected);

    public void RefreshLegs(IReadOnlyList<RouteLeg> legs, string legsEngine)
    {
        Legs = [.. legs];
        LegsEngine = legsEngine;
    }
}
