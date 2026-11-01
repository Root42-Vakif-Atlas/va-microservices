using System.Globalization;
using System.Text.RegularExpressions;
using VakifAtlas.Catalog.Domain;
using VakifAtlas.Catalog.Domain.Works;
using VakifAtlas.ServiceDefaults.Http;
using VakifAtlas.SharedKernel;

namespace VakifAtlas.Catalog.Api.Infrastructure.Reading;

// filters shared by the work list, map tiles and extent: q (every term must match, folded in SQL),
// category / period / city (any of, within a group; groups combine with and), complex (key), keys, bbox
// (s,w,n,e on the work's own location). Publication and Located are used by staff lists only; public reads
// always show published, active works
public sealed partial record WorkQuery(
    string Language,
    IReadOnlyList<string> Terms,
    IReadOnlyList<string> Categories,
    IReadOnlyList<string> Periods,
    IReadOnlyList<short> Cities,
    string? Complex,
    IReadOnlyList<string> Keys,
    (double South, double West, double North, double East)? Bbox,
    string Publication = WorkQuery.Published,
    string Located = WorkQuery.All)
{
    public const int MaxQueryLength = 100;
    public const int MaxTerms = 8;
    public const int MaxKeys = 50;

    public const string All = "all";
    public const string Published = "published";
    public const string Archived = "archived";
    public const string Present = "present";
    public const string Missing = "missing";

    public static Result<WorkQuery> Parse(HttpRequest request)
    {
        var query = request.Query;
        var text = query["q"].ToString();
        var complex = query["complex"].ToString();
        var keys = QueryParsing.CommaList(query["keys"]);
        var categories = QueryParsing.CommaList(query["category"]);
        var periods = QueryParsing.CommaList(query["period"]);
        if (text.Length > MaxQueryLength || (complex.Length > 0 && !CatalogKey.IsValid(complex)) ||
            keys.Count > MaxKeys || !keys.All(CatalogKey.IsValid) ||
            !categories.All(WorkCategory.All.Contains) || !periods.All(WorkPeriod.All.Contains))
        {
            return ApiError.BadRequest();
        }

        var cities = new List<short>();
        foreach (var value in QueryParsing.CommaList(query["city"]))
        {
            if (!short.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var code))
            {
                return ApiError.BadRequest();
            }

            cities.Add(code);
        }

        (double, double, double, double)? bbox = null;
        if (query["bbox"].ToString() is { Length: > 0 } rawBox)
        {
            var parts = rawBox.Split(',').Select(p => double.TryParse(p.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture,
                out var v) && double.IsFinite(v) ? v : double.NaN).ToArray();
            if (parts.Length != 4 || parts.Any(double.IsNaN) || parts[0] > parts[2] || parts[1] > parts[3])
            {
                return ApiError.BadRequest();
            }

            bbox = (parts[0], parts[1], parts[2], parts[3]);
        }

        return new WorkQuery(RequestLanguage.Resolve(request), SplitTerms(text), categories, periods, cities,
            complex.Length > 0 ? complex : null, keys, bbox);
    }

    // words of the query, distinct, at most MaxTerms; folding happens in SQL (catalog_fold), the same function
    // that folds the stored text
    public static IReadOnlyList<string> SplitTerms(string text) =>
        Separators().Split(text).Where(t => t.Length > 0).Distinct(StringComparer.Ordinal).Take(MaxTerms).ToArray();

    public static WorkQuery ForKeys(string language, IReadOnlyList<string> keys) =>
        new(language, [], [], [], [], null, keys, null);

    public WorkQuery WithoutText() => this with { Terms = [] };

    public WorkQuery WithoutCategories() => this with { Categories = [] };

    public WorkQuery WithoutPeriods() => this with { Periods = [] };

    public WorkQuery WithoutCities() => this with { Cities = [] };

    [GeneratedRegex(@"[\s,;]+")]
    private static partial Regex Separators();
}
