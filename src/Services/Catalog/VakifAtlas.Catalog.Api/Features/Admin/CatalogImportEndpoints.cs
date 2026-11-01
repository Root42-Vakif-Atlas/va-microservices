using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using VakifAtlas.Catalog.Api.Infrastructure.Import;
using VakifAtlas.Catalog.Api.Infrastructure.Persistence;
using VakifAtlas.Messaging;
using VakifAtlas.ServiceDefaults.Admin;
using VakifAtlas.ServiceDefaults.Http;
using VakifAtlas.SharedKernel;

namespace VakifAtlas.Catalog.Api.Features.Admin;

// File: root42-veri.json as is. Images: every image src in the file → SHA-256 of the file (computed by the client)
public sealed record ImportRequest(JsonElement? File, Dictionary<string, string>? Images);

public sealed record ImportCounts(int Complexes, int Works, int Parts, int Images, int UniqueImages, int NewWorks, int ExistingWorks);

// validation result (valid = false: nothing written), counts, image hashes not stored yet and warnings;
// Result is set once committed
public sealed record ImportPlan(
    bool Valid,
    ImportCounts? Counts,
    IReadOnlyList<string> MissingImages,
    IReadOnlyList<VgmIssue> Errors,
    IReadOnlyList<VgmIssue> Warnings,
    int WarningCount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ImportResult? Result = null);

public sealed record ImportedImageResponse(string Sha256, string Url, string ThumbnailUrl);

// panel import of a root42 file (catalog/admin/imports). Stateless, nothing is staged in the database:
//   1. POST validate: file + image hashes → plan with the images not stored yet
//   2. PUT images/{sha256}: each missing image (raw bytes), content-addressed, a repeat returns 200
//   3. POST commit: the same body; the whole file in one transaction. Re-running is safe (upsert by key)
public static class CatalogImportEndpoints
{
    // one request per image, so a 3,500-image file uploads in a few minutes
    public const int RequestsPerMinute = 3000;

    // the largest seed file is ~2 MB; with the image map
    public const long MaxPlanBytes = 32L * 1024 * 1024;

    private const int MaxReportedIssues = 500;

    private static readonly string[] ImageTypes = ["image/jpeg", "image/png", "image/webp"];

    public static RouteGroupBuilder MapCatalogImportEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("validate", Validate).WithMetadata(new BodySizeLimit(MaxPlanBytes));
        group.MapPost("commit", Commit).WithIdempotency<ImportRequest>().WithMetadata(new BodySizeLimit(MaxPlanBytes));
        group.MapPut("images/{sha256}", UploadImage).WithMetadata(new BodySizeLimit(ImportImageStore.MaxBytes + 1024));
        return group;
    }

    private static async Task<IResult> Validate(ImportRequest request, CatalogDbContext db, CatalogImporter importer,
        ImportImageStore images, CancellationToken cancellationToken)
    {
        if (request.File is not { ValueKind: JsonValueKind.Object } element || request.Images is null)
        {
            return ApiError.BadRequest(ErrorCodes.InvalidImport).ToHttpResult();
        }

        var (plan, _, _) = await PlanAsync(db, importer, images, element.GetRawText(), request.Images, cancellationToken);
        return TypedResults.Ok(plan);
    }

    private static async Task<IResult> Commit(ImportRequest request, CatalogDbContext db, CatalogImporter importer,
        ImportImageStore images, StaffAudit audit, CancellationToken cancellationToken)
    {
        if (request.File is not { ValueKind: JsonValueKind.Object } element || request.Images is null)
        {
            return ApiError.BadRequest(ErrorCodes.InvalidImport).ToHttpResult();
        }

        var payload = element.GetRawText();
        var (plan, file, media) = await PlanAsync(db, importer, images, payload, request.Images, cancellationToken);
        if (!plan.Valid || file is null)
        {
            return TypedResults.BadRequest(plan);
        }

        if (media is null)
        {
            return TypedResults.Conflict(plan);
        }

        // the audit record commits with the import; the file is identified by its content hash
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        audit.Record("catalog.import-committed", "catalog-import",
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))[..16], new Dictionary<string, string?>
            {
                ["works"] = plan.Counts!.Works.ToString(CultureInfo.InvariantCulture),
                ["complexes"] = plan.Counts.Complexes.ToString(CultureInfo.InvariantCulture),
                ["newWorks"] = plan.Counts.NewWorks.ToString(CultureInfo.InvariantCulture),
            });
        var result = await importer.ImportFileAsync(file, media, cancellationToken, evictCache: false);
        await transaction.CommitAsync(cancellationToken);
        await importer.EvictCacheAsync(cancellationToken);
        return TypedResults.Ok(plan with { Result = result });
    }

    // content-addressed: the body's SHA-256 must equal the path; re-encoded and stored once
    private static async Task<IResult> UploadImage(string sha256, HttpRequest request, ImportImageStore images,
        CancellationToken cancellationToken)
    {
        if (!VgmValidator.Sha256Pattern().IsMatch(sha256) ||
            request.ContentType?.Split(';')[0].Trim().ToLowerInvariant() is not { } type || !ImageTypes.Contains(type))
        {
            return ApiError.BadRequest(ErrorCodes.InvalidUpload).ToHttpResult();
        }

        var stored = await images.StoreAsync(sha256, request.Body, null, cancellationToken);
        if (!stored.IsSuccess)
        {
            return stored.Error.ToHttpResult();
        }

        var (image, created) = stored.Value;
        var response = new ImportedImageResponse(image.Sha256, image.Url, image.ThumbnailUrl);
        return created ? TypedResults.Created((string?)null, response) : TypedResults.Ok(response);
    }

    // parse + validate + database checks + compare with stored images. Media is null while images are missing
    private static async Task<(ImportPlan Plan, VgmFile? File, IReadOnlyDictionary<string, long>? Media)> PlanAsync(
        CatalogDbContext db, CatalogImporter importer, ImportImageStore images, string payload,
        IReadOnlyDictionary<string, string> hashes, CancellationToken cancellationToken)
    {
        static ImportPlan Invalid(IReadOnlyList<VgmIssue> errors, IReadOnlyList<VgmIssue> warnings) =>
            new(false, null, [], errors.Take(MaxReportedIssues).ToArray(), warnings.Take(MaxReportedIssues).ToArray(), warnings.Count);

        var (file, parseError) = VgmValidator.Parse(payload);
        if (file is null)
        {
            return (Invalid([parseError!], []), null, null);
        }

        var cities = (await db.Cities.Select(c => c.Code).ToListAsync(cancellationToken)).ToHashSet();
        var validation = VgmValidator.Validate(file, cities,
            src => hashes.TryGetValue(src, out var sha) && VgmValidator.Sha256Pattern().IsMatch(sha));
        var errors = validation.Errors.Count > 0 ? validation.Errors : await importer.CheckAgainstDatabaseAsync(file, cancellationToken);
        if (errors.Count > 0)
        {
            return (Invalid(errors, validation.Warnings), null, null);
        }

        var sources = VgmValidator.ImageSources(file);
        var unique = sources.Select(s => hashes[s]).Distinct(StringComparer.Ordinal).ToList();
        var stored = await images.FindAsync(unique, cancellationToken);
        var works = file.Works ?? [];
        var keys = works.Select(w => w.Key!).ToList();
        var existing = await db.Works.CountAsync(w => keys.Contains(w.Key), cancellationToken);
        var imageCount = (file.Complexes ?? []).Sum(c => c.Images?.Count ?? 0) +
                         works.Sum(w => (w.Images?.Count ?? 0) + (w.Parts ?? []).Sum(p => p.Images?.Count ?? 0));
        var counts = new ImportCounts((file.Complexes ?? []).Count, works.Count, works.Sum(w => w.Parts?.Count ?? 0), imageCount,
            unique.Count, keys.Count - existing, existing);
        var missing = unique.Where(h => !stored.ContainsKey(h)).ToArray();
        var plan = new ImportPlan(true, counts, missing, [], validation.Warnings.Take(MaxReportedIssues).ToArray(),
            validation.Warnings.Count);
        var media = missing.Length == 0 ? sources.ToDictionary(s => s, s => stored[hashes[s]], StringComparer.Ordinal) : null;
        return (plan, file, media);
    }
}

// per-endpoint body limit (Kestrel's default is 30 MB; smaller for images)
internal sealed class BodySizeLimit(long bytes) : Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata
{
    public long? MaxRequestBodySize => bytes;
}
