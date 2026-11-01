namespace VakifAtlas.SharedKernel;

/// <summary>
/// Represents a geographic coordinate pair in the WGS 84 coordinate reference system (degrees).
/// </summary>
/// <param name="Latitude">The latitude in degrees, valid between -90 and 90.</param>
/// <param name="Longitude">The longitude in degrees, valid between -180 and 180.</param>
public readonly record struct GeoPoint(double Latitude, double Longitude)
{
    /// <summary>
    /// Gets a value indicating whether both latitude and longitude are finite numbers
    /// and lie within standard geographic coordinate boundaries.
    /// </summary>
    public bool IsValid =>
        double.IsFinite(Latitude) && double.IsFinite(Longitude) &&
        Latitude is >= -90 and <= 90 && Longitude is >= -180 and <= 180;
}

/// <summary>
/// Provides common geospatial calculations and unit conversions.
/// </summary>
public static class Geo
{
    private const double EarthRadiusMeters = 6_371_008.8;

    /// <summary>
    /// Calculates the great-circle distance between two geographic coordinates in meters
    /// using the Haversine formula.
    /// </summary>
    /// <param name="a">The origin coordinate point.</param>
    /// <param name="b">The destination coordinate point.</param>
    /// <returns>The calculated distance in meters.</returns>
    public static double DistanceMeters(GeoPoint a, GeoPoint b)
    {
        var dLat = ToRadians(b.Latitude - a.Latitude);
        var dLng = ToRadians(b.Longitude - a.Longitude);
        var h = Math.Pow(Math.Sin(dLat / 2), 2) +
                Math.Cos(ToRadians(a.Latitude)) * Math.Cos(ToRadians(b.Latitude)) * Math.Pow(Math.Sin(dLng / 2), 2);
        return 2 * EarthRadiusMeters * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    /// <summary>
    /// Converts an angle from degrees to radians.
    /// </summary>
    /// <param name="degrees">The angle in degrees.</param>
    /// <returns>The corresponding angle in radians.</returns>
    public static double ToRadians(double degrees) => degrees * Math.PI / 180;
}
