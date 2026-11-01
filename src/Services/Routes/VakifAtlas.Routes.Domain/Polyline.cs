using System.Text;

namespace VakifAtlas.Routes.Domain;

/// <summary>Google encoded polyline (hassasiyet 5) — istemcideki <c>PolylineCodec</c> ile aynı.</summary>
public static class Polyline
{
    private const double Factor = 1e5;

    public static IReadOnlyList<(double Latitude, double Longitude)> Decode(string encoded)
    {
        var points = new List<(double, double)>();
        int index = 0, lat = 0, lng = 0;
        while (index < encoded.Length)
        {
            lat += NextValue(encoded, ref index);
            lng += NextValue(encoded, ref index);
            points.Add((lat / Factor, lng / Factor));
        }

        return points;
    }

    public static string Encode(IEnumerable<(double Latitude, double Longitude)> points)
    {
        var builder = new StringBuilder();
        int previousLat = 0, previousLng = 0;
        foreach (var (latitude, longitude) in points)
        {
            var lat = (int)Math.Round(latitude * Factor, MidpointRounding.AwayFromZero);
            var lng = (int)Math.Round(longitude * Factor, MidpointRounding.AwayFromZero);
            Write(builder, lat - previousLat);
            Write(builder, lng - previousLng);
            (previousLat, previousLng) = (lat, lng);
        }

        return builder.ToString();
    }

    public static string Reverse(string encoded) => Encode(Decode(encoded).Reverse());

    private static int NextValue(string encoded, ref int index)
    {
        int result = 0, shift = 0, chunk;
        do
        {
            chunk = encoded[index++] - 63;
            result |= (chunk & 0x1F) << shift;
            shift += 5;
        }
        while (chunk >= 0x20 && index < encoded.Length);

        return (result & 1) != 0 ? ~(result >> 1) : result >> 1;
    }

    private static void Write(StringBuilder builder, int value)
    {
        var shifted = value < 0 ? ~(value << 1) : value << 1;
        while (shifted >= 0x20)
        {
            builder.Append((char)((0x20 | (shifted & 0x1F)) + 63));
            shifted >>= 5;
        }

        builder.Append((char)(shifted + 63));
    }
}
