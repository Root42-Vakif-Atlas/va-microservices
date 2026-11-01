using VakifAtlas.Catalog.Api.Infrastructure;
using VakifAtlas.Catalog.Api.Infrastructure.Reading;
using VakifAtlas.Catalog.Domain;
using VakifAtlas.Catalog.Domain.Works;
using VakifAtlas.ServiceDefaults.Http;
using VakifAtlas.SharedKernel;

namespace VakifAtlas.Catalog.Api.Features.Complexes;

// GET catalog/complexes, GET catalog/complexes/{key} (with the first page of works),
// GET catalog/complexes/{key}/works (category filter). Unknown or hidden complex → 404
public static class ComplexEndpoints
{
    public static RouteGroupBuilder MapComplexEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("complexes", List)
            .CacheOutput(CatalogCaching.ListPolicy)
            .WithCacheHeaders(CatalogCaching.ListMaxAge);
        group.MapGet("complexes/{key}", Detail)
            .CacheOutput(CatalogCaching.ListPolicy)
            .WithCacheHeaders(CatalogCaching.ListMaxAge);
        group.MapGet("complexes/{key}/works", Works)
            .CacheOutput(CatalogCaching.ListPolicy)
            .WithCacheHeaders(CatalogCaching.ListMaxAge);
        return group;
    }

    private static async Task<IResult> List(HttpRequest request, CatalogReadStore store, CancellationToken cancellationToken)
    {
        var limit = QueryParsing.Limit(request.Query["limit"], 20, 50);
        if (limit is null)
        {
            return ApiError.BadRequest().ToHttpResult();
        }

        var page = await store.ComplexesAsync(RequestLanguage.Resolve(request), limit.Value, request.Query["cursor"].ToString(),
            cancellationToken);
        return page.IsSuccess ? TypedResults.Ok(page.Value) : page.Error.ToHttpResult();
    }

    private static async Task<IResult> Detail(string key, HttpRequest request, CatalogReadStore store,
        CancellationToken cancellationToken)
    {
        var detail = CatalogKey.IsValid(key)
            ? await store.ComplexDetailAsync(key, RequestLanguage.Resolve(request), cancellationToken)
            : null;
        return detail is null ? ApiError.NotFound().ToHttpResult() : TypedResults.Ok(detail);
    }

    private static async Task<IResult> Works(string key, HttpRequest request, CatalogReadStore store,
        CancellationToken cancellationToken)
    {
        var limit = QueryParsing.Limit(request.Query["limit"], 20, 50);
        if (limit is null)
        {
            return ApiError.BadRequest().ToHttpResult();
        }

        var categories = QueryParsing.CommaList(request.Query["category"]);
        if (!categories.All(WorkCategory.All.Contains))
        {
            return ApiError.BadRequest().ToHttpResult();
        }

        if (!CatalogKey.IsValid(key) || !await store.ComplexExistsAsync(key, cancellationToken))
        {
            return ApiError.NotFound().ToHttpResult();
        }

        var page = await store.WorksOfAsync(key, RequestLanguage.Resolve(request), limit.Value,
            request.Query["cursor"].ToString(), cancellationToken, categories);
        return page.IsSuccess ? TypedResults.Ok(page.Value) : page.Error.ToHttpResult();
    }
}
