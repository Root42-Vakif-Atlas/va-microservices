using VakifAtlas.Catalog.Api.Infrastructure;
using VakifAtlas.Catalog.Api.Infrastructure.Reading;
using VakifAtlas.Catalog.Domain.Map;
using VakifAtlas.ServiceDefaults.Http;
using VakifAtlas.SharedKernel;

namespace VakifAtlas.Catalog.Api.Features.Map;

// server-side clustering: GET catalog/map/tiles/{z}/{x}/{y} and GET catalog/map/extent, with the work list's
// filters. Response size does not grow with the catalog; tile URLs carry v (the catalog version from extent)
// and are cached a day at the CDN
public static class MapEndpoints
{
    public static RouteGroupBuilder MapMapEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("map/tiles/{z}/{x}/{y}", Tile)
            .CacheOutput(CatalogCaching.TilePolicy)
            .WithCacheHeaders(CatalogCaching.TileMaxAge);
        group.MapGet("map/extent", Extent)
            .CacheOutput(CatalogCaching.ExtentPolicy)
            .WithCacheHeaders(CatalogCaching.ExtentMaxAge);
        return group;
    }

    private static async Task<IResult> Tile(string z, string x, string y, HttpRequest request, CatalogReadStore store,
        CancellationToken cancellationToken)
    {
        if (!int.TryParse(z, out var zoom) || !int.TryParse(x, out var tileX) || !int.TryParse(y, out var tileY) ||
            !MapTileMath.IsValidTile(zoom, tileX, tileY))
        {
            return ApiError.BadRequest().ToHttpResult();
        }

        var query = WorkQuery.Parse(request);
        return query.IsSuccess
            ? TypedResults.Ok(new TileResponse(await store.TileAsync(query.Value, zoom, tileX, tileY, cancellationToken)))
            : query.Error.ToHttpResult();
    }

    private static async Task<IResult> Extent(HttpRequest request, CatalogReadStore store, CancellationToken cancellationToken)
    {
        var query = WorkQuery.Parse(request);
        return query.IsSuccess
            ? TypedResults.Ok(await store.ExtentAsync(query.Value, cancellationToken))
            : query.Error.ToHttpResult();
    }
}
