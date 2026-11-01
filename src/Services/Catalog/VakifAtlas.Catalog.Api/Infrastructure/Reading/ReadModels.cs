using System.Text.Json.Serialization;

namespace VakifAtlas.Catalog.Api.Infrastructure.Reading;

// public responses; texts are in the request language and fall back to tr when missing

public sealed record Location(double Latitude, double Longitude);

public sealed record CityRef(short Code, string Name);

public sealed record ComplexRef(string Key, string Name);

// licence credit is always present (own images: the institution)
public sealed record ImageResponse(
    string Url, string ThumbnailUrl, string? AltText, string Author, string License, string SourceName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SourceUrl = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Title = null);

// Location null: not entered yet; the work is listed but not on the map or in route planning
public sealed record WorkSummary(
    string Key, string Slug, string Title,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Nickname,
    string Category, string Period, CityRef City, string? District, string? Address, Location? Location,
    string? ShortDescription, string? ThumbnailUrl,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ComplexRef? Complex = null);

// view=location: the light shape for route planning and map lists
public sealed record WorkLocation(
    string Key, string Slug, string Title, string Category, CityRef City, string? District, Location? Location,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ComplexRef? Complex = null);

public sealed record CityFacet(short Code, string Name, int Count);

// disjunctive counts: each group is counted with every filter except its own selection
public sealed record Facets(
    IReadOnlyDictionary<string, int> Category,
    IReadOnlyDictionary<string, int> Period,
    IReadOnlyList<CityFacet> City);

// a city or district matching the search; Bounds [s, w, n, e] of its works on the map
public sealed record Place(
    CityRef City,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? District,
    int Count,
    double[] Bounds);

// items: WorkSummary, or WorkLocation with view=location
public sealed record WorkPage(
    IReadOnlyList<object> Items, int Total, string? NextCursor,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<Place>? Places = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Facets? Facets = null);

// URL slug per language (tr = key, en = en slug or key): for language switch and hreflang
public sealed record WorkSlugs(string Tr, string En);

public sealed record PartResponse(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Title,
    string? Description, IReadOnlyList<ImageResponse> Images);

public sealed record WorkDetail(
    string Key, string Slug, WorkSlugs Slugs, string Title,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Nickname,
    string Category, string Period, CityRef City, string? District, string? Address, Location? Location,
    string? ShortDescriptionTitle, string? ShortDescription, string? History, string? ConstructionDate, string? Founder,
    string? Architect, bool IsUnescoWorldHeritage, IReadOnlyList<ImageResponse> Images, IReadOnlyList<PartResponse> Parts,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ComplexRef? Complex = null);

public sealed record ComplexSummary(
    string Key, string Name, CityRef City, string District, string? Address, Location? Location, string? ThumbnailUrl,
    int WorkCount);

public sealed record ComplexPage(IReadOnlyList<ComplexSummary> Items, int Total, string? NextCursor);

// Footprint [[lat, lng], …] (counter-clockwise, empty below three located points) and Bounds [s, w, n, e] are
// derived from the complex's location and its works'; the first page of works is embedded (one round trip)
public sealed record ComplexDetail(
    string Key, string Name, CityRef City, string District, string? Address, Location? Location,
    IReadOnlyList<ImageResponse> Images, int WorkCount, double[][] Footprint, double[]? Bounds, WorkPage Works);

// map tile items, told apart by Type
public sealed record TileCluster(string Key, int Count, double Lat, double Lng, double[] Bounds)
{
    public string Type => "cluster";
}

public sealed record TileWork(
    string Key, string Slug, string Title, string Category, CityRef City, string? District, double Lat, double Lng,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ComplexRef? Complex = null)
{
    public string Type => "work";
}

// Footprint only in expanded tiles (z ≥ MapTileMath.ComplexExpandZoom)
public sealed record TileComplex(
    string Key, string Name, CityRef City, string District, double Lat, double Lng, int WorkCount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double[][]? Footprint = null)
{
    public string Type => "complex";
}

public sealed record TileResponse(IReadOnlyList<object> Items);

// what the map shows for a query: Total = works on the map (own location or through their complex)
public sealed record Extent(int Total, double[]? Bounds, string Version);
