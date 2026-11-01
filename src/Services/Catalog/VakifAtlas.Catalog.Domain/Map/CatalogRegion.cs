namespace VakifAtlas.Catalog.Domain.Map;

// rectangle covering Türkiye's land and coast (same region as the map camera); locations outside are typos
public static class CatalogRegion
{
    public static bool Contains(double latitude, double longitude) =>
        latitude is >= 35.5 and <= 42.5 && longitude is >= 25.5 and <= 45.0;
}
