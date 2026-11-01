using VakifAtlas.SharedKernel;

namespace VakifAtlas.Catalog.Domain.Map;

// complex footprint, same algorithm as the client test mock (MockComplexGeometry): convex hull of the points
// (Andrew's monotone chain), each corner pushed BufferMeters out from the centroid. Fewer than three distinct
// points → no footprint
public static class ComplexGeometry
{
    public const double BufferMeters = 25;

    private const double MetersPerDegree = 111320;

    // counter-clockwise corners, rounded to 6 decimals
    public static IReadOnlyList<GeoPoint> Footprint(IEnumerable<GeoPoint> points)
    {
        var unique = points.Select(p => (X: p.Longitude, Y: p.Latitude)).Distinct()
            .OrderBy(p => p.X).ThenBy(p => p.Y).ToArray();
        if (unique.Length < 3)
        {
            return [];
        }

        static double Cross((double X, double Y) o, (double X, double Y) a, (double X, double Y) b) =>
            ((a.X - o.X) * (b.Y - o.Y)) - ((a.Y - o.Y) * (b.X - o.X));

        var lower = new List<(double X, double Y)>();
        foreach (var point in unique)
        {
            while (lower.Count >= 2 && Cross(lower[^2], lower[^1], point) <= 0)
            {
                lower.RemoveAt(lower.Count - 1);
            }

            lower.Add(point);
        }

        var upper = new List<(double X, double Y)>();
        foreach (var point in unique.Reverse())
        {
            while (upper.Count >= 2 && Cross(upper[^2], upper[^1], point) <= 0)
            {
                upper.RemoveAt(upper.Count - 1);
            }

            upper.Add(point);
        }

        var hull = lower.Take(lower.Count - 1).Concat(upper.Take(upper.Count - 1)).ToArray();
        if (hull.Length < 3)
        {
            return [];
        }

        var centerX = hull.Average(p => p.X);
        var centerY = hull.Average(p => p.Y);
        var lngScale = Math.Cos(centerY * Math.PI / 180);
        return hull.Select(p => Offset(p.Y, p.X, centerY, centerX, lngScale)).ToArray();
    }

    // [south, west, north, east]; null without points
    public static double[]? Bounds(IReadOnlyCollection<GeoPoint> points) => points.Count == 0
        ? null
        : [points.Min(p => p.Latitude), points.Min(p => p.Longitude), points.Max(p => p.Latitude), points.Max(p => p.Longitude)];

    private static GeoPoint Offset(double lat, double lng, double centerLat, double centerLng, double lngScale)
    {
        var dy = (lat - centerLat) * MetersPerDegree;
        var dx = (lng - centerLng) * MetersPerDegree * lngScale;
        var length = Math.Sqrt((dx * dx) + (dy * dy));
        if (length == 0)
        {
            return new GeoPoint(Round(lat), Round(lng));
        }

        var scale = BufferMeters / length;
        return new GeoPoint(
            Round(lat + (dy * scale / MetersPerDegree)),
            Round(lng + (dx * scale / (MetersPerDegree * lngScale))));
    }

    private static double Round(double value) => Math.Round(value * 1e6, MidpointRounding.AwayFromZero) / 1e6;
}
