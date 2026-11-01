using VakifAtlas.Catalog.Api.Infrastructure;
using VakifAtlas.Catalog.Api.Infrastructure.Reading;
using VakifAtlas.Catalog.Domain;
using VakifAtlas.ServiceDefaults.Http;
using VakifAtlas.SharedKernel;

namespace VakifAtlas.Catalog.Api.Features.Works;

// GET catalog/works: the one collection resource (list, search, filters, locations). view=summary (default) or
// location (light shape, larger pages, for route planning); include=places,facets on the first page.
// GET catalog/works/{slug}: detail by the tr URL (key) or the en slug
public static class WorkEndpoints
{
    private static readonly Dictionary<string, (int Default, int Max)> Limits = new(StringComparer.Ordinal)
    {
        ["summary"] = (20, 50),
        ["location"] = (1000, 2000),
    };

    public static RouteGroupBuilder MapWorkEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("works", List)
            .CacheOutput(CatalogCaching.ListPolicy)
            .WithCacheHeaders(CatalogCaching.ListMaxAge);
        group.MapGet("works/{slug}", Detail)
            .CacheOutput(CatalogCaching.ListPolicy)
            .WithCacheHeaders(CatalogCaching.ListMaxAge);
        return group;
    }

    private static async Task<IResult> List(HttpRequest request, CatalogReadStore store, CancellationToken cancellationToken)
    {
        var view = request.Query["view"].ToString() is { Length: > 0 } requested ? requested : "summary";
        if (!Limits.TryGetValue(view, out var limits))
        {
            return ApiError.BadRequest().ToHttpResult();
        }

        var limit = QueryParsing.Limit(request.Query["limit"], limits.Default, limits.Max);
        var parsed = WorkQuery.Parse(request);
        if (limit is null || !parsed.IsSuccess)
        {
            return (parsed.Error ?? ApiError.BadRequest()).ToHttpResult();
        }

        var query = parsed.Value;
        var cursor = request.Query["cursor"].ToString();
        var page = await store.SearchIdsAsync(query, limit.Value, cursor, publicOnly: true, cancellationToken);
        if (!page.IsSuccess)
        {
            return page.Error.ToHttpResult();
        }

        var summaries = await store.SummariesAsync(page.Value.Ids, query.Language, cancellationToken);
        var total = await store.CountAsync(query, publicOnly: true, cancellationToken);
        var firstPage = string.IsNullOrEmpty(cursor);
        var include = QueryParsing.CommaList(request.Query["include"]);
        var places = firstPage && include.Contains("places") ? await store.PlacesAsync(query, cancellationToken) : null;
        var facets = firstPage && include.Contains("facets") ? await store.FacetsAsync(query, cancellationToken) : null;
        IReadOnlyList<object> items = view == "location"
            ? summaries.Select(CatalogReadStore.ToLocation).ToArray()
            : summaries.ToArray<object>();
        return TypedResults.Ok(new WorkPage(items, total, page.Value.NextCursor, places, facets));
    }

    private static async Task<IResult> Detail(string slug, HttpRequest request, CatalogReadStore store,
        CancellationToken cancellationToken)
    {
        var detail = CatalogKey.IsValid(slug)
            ? await store.WorkDetailAsync(slug, RequestLanguage.Resolve(request), cancellationToken)
            : null;
        return detail is null ? ApiError.NotFound().ToHttpResult() : TypedResults.Ok(detail);
    }
}
