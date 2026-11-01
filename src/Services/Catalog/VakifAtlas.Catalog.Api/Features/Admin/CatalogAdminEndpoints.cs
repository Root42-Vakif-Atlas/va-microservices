using VakifAtlas.Catalog.Api.Infrastructure.Reading;
using VakifAtlas.Catalog.Domain;
using VakifAtlas.Media;
using VakifAtlas.ServiceDefaults.Admin;
using VakifAtlas.ServiceDefaults.Http;
using VakifAtlas.SharedKernel;

namespace VakifAtlas.Catalog.Api.Features.Admin;

// catalog administration (catalog/admin, VGM staff). Writes go through the importer like a file import;
// changes take If-Match, creates take Idempotency-Key, deletes are soft (restore brings the row back).
// Not cached; inactive and archived rows are included
public static class CatalogAdminEndpoints
{
    public static RouteGroupBuilder MapCatalogAdminEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("works", ListWorks);
        group.MapGet("works/{key}", GetWork);
        group.MapPost("works", (AdminWorkInput input, HttpContext http, CatalogAdminService service, CancellationToken ct) =>
            service.SaveWorkAsync(null, input, http, ct)).WithIdempotency<AdminWorkInput>();
        group.MapPut("works/{key}", (string key, AdminWorkInput input, HttpContext http, CatalogAdminService service,
            CancellationToken ct) => service.SaveWorkAsync(key, input, http, ct));
        group.MapDelete("works/{key}", (string key, HttpContext http, CatalogAdminService service, CancellationToken ct) =>
            service.SetWorkPublicationAsync(key, archived: true, http, ct));
        group.MapPost("works/{key}/restore", (string key, HttpContext http, CatalogAdminService service, CancellationToken ct) =>
            service.SetWorkPublicationAsync(key, archived: false, http, ct));

        group.MapGet("complexes", ListComplexes);
        group.MapGet("complexes/{key}", GetComplex);
        group.MapPost("complexes", (AdminComplexInput input, HttpContext http, CatalogAdminService service, CancellationToken ct) =>
            service.SaveComplexAsync(null, input, http, ct)).WithIdempotency<AdminComplexInput>();
        group.MapPut("complexes/{key}", (string key, AdminComplexInput input, HttpContext http, CatalogAdminService service,
            CancellationToken ct) => service.SaveComplexAsync(key, input, http, ct));
        group.MapDelete("complexes/{key}", (string key, HttpContext http, CatalogAdminService service, CancellationToken ct) =>
            service.SetComplexPublicationAsync(key, archived: true, http, ct));
        group.MapPost("complexes/{key}/restore", (string key, HttpContext http, CatalogAdminService service, CancellationToken ct) =>
            service.SetComplexPublicationAsync(key, archived: false, http, ct));
        group.MapPut("complexes/{key}/works", SetComplexWorks);

        group.MapPost("image-uploads", (PhotoUploadRequest request, HttpContext http, CatalogAdminService service,
            CancellationToken ct) => service.RequestUploadAsync(request, http, ct));
        group.MapGet("summary", (CatalogAdminService service, CancellationToken ct) => service.SummaryAsync(ct));
        return group;
    }

    // the public list's filters (q also searches founder, architect, construction date) plus
    // publication (all | published | archived), location (all | present | missing: the to-do list of works
    // still off the map) and sort (name | updated); Turkish titles
    private static async Task<IResult> ListWorks(HttpRequest request, CatalogReadStore store, CatalogAdminService service,
        CancellationToken cancellationToken)
    {
        var limit = QueryParsing.Limit(request.Query["limit"], 50, 100);
        var parsed = WorkQuery.Parse(request);
        var publication = Choice(request, "publication", WorkQuery.All);
        var located = Choice(request, "location", WorkQuery.All);
        var sort = Choice(request, "sort", "name");
        if (limit is null || !parsed.IsSuccess ||
            publication is not (WorkQuery.All or WorkQuery.Published or WorkQuery.Archived) ||
            located is not (WorkQuery.All or WorkQuery.Present or WorkQuery.Missing) || sort is not ("name" or "updated"))
        {
            return (parsed.Error ?? ApiError.BadRequest()).ToHttpResult();
        }

        var query = parsed.Value with { Language = "tr", Publication = publication, Located = located };
        var page = await store.SearchIdsAsync(query, limit.Value, request.Query["cursor"].ToString(), publicOnly: false,
            cancellationToken, sort == "updated" ? WorkOrder.Updated : WorkOrder.Relevance);
        if (!page.IsSuccess)
        {
            return page.Error.ToHttpResult();
        }

        var total = await store.CountAsync(query, publicOnly: false, cancellationToken);
        var items = await service.WorkSummariesAsync(page.Value.Ids, cancellationToken);
        return TypedResults.Ok(new Page<AdminWorkSummary>(items, total, page.Value.NextCursor));
    }

    private static async Task<IResult> GetWork(string key, HttpContext context, CatalogAdminService service,
        CancellationToken cancellationToken)
    {
        var detail = await service.LoadWorkAsync(key, cancellationToken);
        if (detail is null)
        {
            return ApiError.NotFound().ToHttpResult();
        }

        context.Response.SetETag(detail.Value.ETag);
        return TypedResults.Ok(detail.Value.Detail);
    }

    private static async Task<IResult> ListComplexes(HttpRequest request, CatalogAdminService service,
        CancellationToken cancellationToken)
    {
        var parsed = WorkQuery.Parse(request);
        return parsed.IsSuccess
            ? TypedResults.Ok(await service.ComplexesAsync(parsed.Value.Cities, cancellationToken))
            : parsed.Error.ToHttpResult();
    }

    private static async Task<IResult> GetComplex(string key, HttpContext context, CatalogAdminService service,
        CancellationToken cancellationToken)
    {
        var detail = await service.LoadComplexAsync(key, cancellationToken);
        if (detail is null)
        {
            return ApiError.NotFound().ToHttpResult();
        }

        context.Response.SetETag(detail.Value.ETag);
        return TypedResults.Ok(detail.Value.Detail);
    }

    // ordered work keys (position = index); works left out become standalone
    private static async Task<IResult> SetComplexWorks(string key, ComplexWorksRequest request, HttpContext context,
        CatalogAdminService service, CancellationToken cancellationToken)
    {
        if (request.WorkKeys is null || request.WorkKeys.Count > AdminRules.MaxMembers || !request.WorkKeys.All(CatalogKey.IsValid))
        {
            return ApiError.BadRequest(ErrorCodes.InvalidComplexMembers).ToHttpResult();
        }

        return await service.SetComplexWorksAsync(key, request.WorkKeys, context, cancellationToken);
    }

    private static string Choice(HttpRequest request, string name, string fallback) =>
        request.Query[name].ToString() is { Length: > 0 } value ? value : fallback;
}
