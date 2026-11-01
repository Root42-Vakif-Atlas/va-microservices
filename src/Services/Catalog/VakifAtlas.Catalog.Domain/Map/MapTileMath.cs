namespace VakifAtlas.Catalog.Domain.Map;

// Web Mercator XYZ tile math, same formulas as the client (core/map/map_tiles.dart) and the generated
// mercator_x / mercator_y columns. A tile is split into 4×4 cells (≈64 px ≥ 48 dp) for clustering
public static class MapTileMath
{
    public const int MinZoom = 4;
    public const int MaxZoom = 18;

    // from here on every marker is returned on its own (no clusters)
    public const int SinglesZoom = 15;

    // from here on complexes open: footprint plus the pins of their works. Further out a complex with a
    // location stands for its works
    public const int ComplexExpandZoom = 17;

    public const int CellsPerSide = 4;

    public const double MaxLatitude = 85.05112878;

    // WGS 84 → unit square (0–1)
    public static (double X, double Y) Project(double latitude, double longitude)
    {
        var sin = Math.Sin(Math.Clamp(latitude, -MaxLatitude, MaxLatitude) * Math.PI / 180);
        var y = 0.5 - (Math.Log((1 + sin) / (1 - sin)) / (4 * Math.PI));
        return ((longitude + 180) / 360, Math.Clamp(y, 0, 1));
    }

    public static bool IsValidTile(int z, int x, int y) =>
        z is >= MinZoom and <= MaxZoom && x >= 0 && y >= 0 && x < 1 << z && y < 1 << z;

    // unit Mercator bounds of a tile: [minX, maxX) × [minY, maxY)
    public static (double MinX, double MaxX, double MinY, double MaxY) TileBounds(int z, int x, int y)
    {
        double count = 1 << z;
        return (x / count, (x + 1) / count, y / count, (y + 1) / count);
    }

    // cells per side of the clustering grid (2^z × 4)
    public static int CellCount(int z) => (1 << z) * CellsPerSide;
}
