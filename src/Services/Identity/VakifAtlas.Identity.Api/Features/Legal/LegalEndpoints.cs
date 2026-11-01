using Microsoft.EntityFrameworkCore;
using VakifAtlas.Identity.Api.Infrastructure;
using VakifAtlas.Identity.Domain;
using VakifAtlas.ServiceDefaults.Http;
using VakifAtlas.SharedKernel;

namespace VakifAtlas.Identity.Api.Features.Legal;

public sealed record LegalDocumentResponse(string Type, string Version, string Title, string Body, DateOnly EffectiveDate);

public static class LegalEndpoints
{
    /// <summary><c>GET legal/documents/{terms-of-use|privacy-notice}?lang=tr|en</c>.</summary>
    public static RouteGroupBuilder MapLegalEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("documents/{type}", async (string type, string? lang, IdentityDbContext db, CancellationToken cancellationToken) =>
        {
            if (!LegalDocument.Types.Contains(type))
            {
                return ApiError.NotFound().ToHttpResult();
            }

            var language = RequestLanguage.Normalize(lang);
            var document = await db.LegalDocuments.AsNoTracking()
                .SingleOrDefaultAsync(d => d.Type == type && d.Language == language, cancellationToken);
            return document is null
                ? ApiError.NotFound().ToHttpResult()
                : TypedResults.Ok(new LegalDocumentResponse(document.Type, document.Version, document.Title, document.Body,
                    document.EffectiveDate));
        }).AllowAnonymous();
        return group;
    }
}
