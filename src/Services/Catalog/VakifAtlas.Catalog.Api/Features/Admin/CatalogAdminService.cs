using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using VakifAtlas.Catalog.Api.Infrastructure;
using VakifAtlas.Catalog.Api.Infrastructure.Import;
using VakifAtlas.Catalog.Api.Infrastructure.Mirroring;
using VakifAtlas.Catalog.Api.Infrastructure.Persistence;
using VakifAtlas.Catalog.Api.Infrastructure.Reading;
using VakifAtlas.Catalog.Domain;
using VakifAtlas.Catalog.Domain.Map;
using VakifAtlas.Catalog.Domain.References;
using VakifAtlas.Catalog.Domain.Works;
using VakifAtlas.Media;
using VakifAtlas.Messaging;
using VakifAtlas.ServiceDefaults.Admin;
using VakifAtlas.ServiceDefaults.Auth;
using VakifAtlas.ServiceDefaults.Http;
using VakifAtlas.SharedKernel;

namespace VakifAtlas.Catalog.Api.Features.Admin;

// staff writes: validation, row lock + If-Match (revision), image resolution, then the importer (version, events)
// with the audit record in the same transaction; the public cache is evicted after commit. Deleting is soft
// (deleted_at) and can be restored
public sealed class CatalogAdminService(
    CatalogDbContext db,
    CatalogImporter importer,
    PhotoUploadService<CatalogDbContext> uploads,
    IOptions<StorageOptions> storage,
    MediaUrls urls,
    ImageMirrorSignal mirror,
    StaffAudit audit)
{
    public async Task<(AdminWorkDetail Detail, string ETag)?> LoadWorkAsync(string key, CancellationToken cancellationToken)
    {
        var work = await db.Works.AsNoTracking().Where(w => w.Key == key).IncludeContent().SingleOrDefaultAsync(cancellationToken);
        if (work is null)
        {
            return null;
        }

        static AdminWorkText? Text(WorkTranslation? t) => t is null
            ? null
            : new AdminWorkText(t.Slug, t.Title, t.Nickname, t.ShortDescriptionTitle, t.ShortDescription, t.History,
                t.ConstructionDate, t.Founder, t.Architect);

        var complex = work.Complex is { } c
            ? new AdminComplexRef(c.Key, c.Translations.SingleOrDefault(t => t.Locale == "tr")?.Name ?? c.Key, work.ComplexPosition)
            : null;
        var detail = new AdminWorkDetail(
            work.Key, work.CityCode, work.District, work.Address, work.Category, work.CategoryOverridden, work.Period,
            work.IsUnescoWorldHeritage, work.Location is { } point ? new Location(point.Latitude, point.Longitude) : null,
            work.IsActive,
            new VgmLanguages<AdminWorkText>(Text(work.Translations.SingleOrDefault(t => t.Locale == "tr")),
                Text(work.Translations.SingleOrDefault(t => t.Locale == "en"))),
            work.Images.OrderBy(i => i.Position)
                .Select(i => Image(i.Media, i.Translations.Select(t => (t.Locale, t.AltText, t.Title)))).ToArray(),
            work.Parts.OrderBy(p => p.Position).Select(p => new AdminPart(
                new VgmLanguages<VgmPartText>(
                    p.Translations.Where(t => t.Locale == "tr").Select(t => new VgmPartText(t.Title, t.Description)).SingleOrDefault(),
                    p.Translations.Where(t => t.Locale == "en").Select(t => new VgmPartText(t.Title, t.Description)).SingleOrDefault()),
                p.Images.OrderBy(i => i.Position)
                    .Select(i => Image(i.Media, i.Translations.Select(t => (t.Locale, t.AltText, t.Title)))).ToArray()))
                .ToArray(),
            complex, AdminRules.PublicationOf(work.DeletedAt), work.DeletedAt, work.UpdatedAt);
        return (detail, AdminRules.ETagOf(work.Revision));
    }

    // summaries in the order of ids (staff list: inactive and archived works included)
    public async Task<IReadOnlyList<AdminWorkSummary>> WorkSummariesAsync(IReadOnlyList<long> ids, CancellationToken cancellationToken)
    {
        var rows = await db.Works.AsNoTracking().Where(w => ids.Contains(w.Id)).Select(w => new
        {
            w.Id,
            w.Key,
            w.Category,
            w.Period,
            w.CityCode,
            City = w.City.Name,
            w.District,
            HasLocation = w.Latitude != null,
            w.IsActive,
            w.DeletedAt,
            w.UpdatedAt,
            w.ComplexPosition,
            Tr = w.Translations.Where(t => t.Locale == "tr").Select(t => t.Title).FirstOrDefault(),
            En = w.Translations.Where(t => t.Locale == "en").Select(t => t.Title).FirstOrDefault(),
            Cover = w.Images.OrderBy(i => i.Position).Select(i => i.Media).FirstOrDefault(),
            ImageCount = w.Images.Count + w.Parts.SelectMany(p => p.Images).Count(),
            ComplexKey = w.Complex != null ? w.Complex.Key : null,
            ComplexName = w.Complex!.Translations.Where(t => t.Locale == "tr").Select(t => t.Name).FirstOrDefault(),
        }).ToDictionaryAsync(w => w.Id, cancellationToken);

        return ids.Where(rows.ContainsKey).Select(id => rows[id]).Select(w => new AdminWorkSummary(
            w.Key, new VgmName(w.Tr, w.En), w.Category, w.Period, new CityRef(w.CityCode, w.City), w.District,
            w.Cover is null ? null : urls.ThumbnailUrl(MediaRef.Of(w.Cover)), w.ImageCount, w.HasLocation,
            w.ComplexKey is null ? null : new AdminComplexRef(w.ComplexKey, w.ComplexName ?? w.ComplexKey, w.ComplexPosition),
            w.IsActive, AdminRules.PublicationOf(w.DeletedAt), w.UpdatedAt)).ToArray();
    }

    // create (key null) or full replace
    public async Task<IResult> SaveWorkAsync(string? key, AdminWorkInput input, HttpContext context, CancellationToken cancellationToken)
    {
        var creating = key is null;
        var targetKey = key ?? input.Key;
        if (!creating && input.Key is not null && input.Key != key)
        {
            return Invalid("invalid_work", [new VgmIssue("key", "key_mismatch", input.Key)]);
        }

        if (!creating && !context.Request.HasIfMatch())
        {
            return ApiError.PreconditionRequired().ToHttpResult();
        }

        var sources = new List<ImageSource>();
        var work = new VgmWork(targetKey, input.CityCode, input.Address, input.IsActive,
            new VgmLanguages<VgmWorkText>(FileText(input.Translations?.Tr), FileText(input.Translations?.En)),
            Images(input.Images, "images", sources),
            input.Parts?.Select((p, i) => new VgmPart(p.Translations, Images(p.Images, $"parts[{i}].images", sources))).ToList());
        var file = new VgmFile(null, VgmValidator.FormatVersion, [], [work]);
        var errors = (await ValidateAsync(file, "works[0].", cancellationToken)).Concat(ValidateDetails(input)).Concat(Validate(sources)).ToList();
        if (errors.Count > 0)
        {
            return Invalid("invalid_work", errors);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var existing = await db.Works.FromSql($"SELECT * FROM works WHERE key = {targetKey} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (creating && existing is not null)
        {
            return ApiError.Conflict(ErrorCodes.WorkExists).ToHttpResult();
        }

        if (!creating)
        {
            if (existing is null)
            {
                return ApiError.NotFound().ToHttpResult();
            }

            if (context.Request.CheckIfMatch(AdminRules.ETagOf(existing.Revision)) is { } precondition)
            {
                return precondition.ToHttpResult();
            }

            // the complex FK pins a member's city to its complex's city
            if (existing.ComplexId is not null && existing.CityCode != input.CityCode)
            {
                return ApiError.Conflict(ErrorCodes.InvalidComplexMembers).ToHttpResult();
            }
        }

        var resolved = await ResolveImagesAsync(targetKey!, sources, context, cancellationToken);
        if (!resolved.IsSuccess)
        {
            return resolved.Error.ToHttpResult();
        }

        var details = new WorkDetails(input.Category, input.Period ?? WorkPeriod.Other, input.IsUnescoWorldHeritage ?? false,
            input.District, PointOf(input.Location),
            new VgmLanguages<WorkStaffText>(StaffText(input.Translations?.Tr), StaffText(input.Translations?.En)));
        audit.Record(creating ? "work.created" : "work.updated", "work", targetKey!, new Dictionary<string, string?>
        {
            ["category"] = input.Category,
            ["images"] = sources.Count.ToString(CultureInfo.InvariantCulture),
        });
        await CommitAsync(() => importer.SaveWorkAsync(work, details, resolved.Value.Media, cancellationToken), transaction,
            resolved.Value.Claimed, cancellationToken);
        if (sources.Any(s => s.Input.Url is not null))
        {
            mirror.Notify();
        }

        var detail = (await LoadWorkAsync(targetKey!, cancellationToken))!.Value;
        context.Response.SetETag(detail.ETag);
        return creating ? TypedResults.Created($"works/{targetKey}", detail.Detail) : TypedResults.Ok(detail.Detail);
    }

    public async Task<IResult> SetWorkPublicationAsync(string key, bool archived, HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!context.Request.HasIfMatch())
        {
            return ApiError.PreconditionRequired().ToHttpResult();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var work = await db.Works.FromSql($"SELECT * FROM works WHERE key = {key} FOR UPDATE").SingleOrDefaultAsync(cancellationToken);
        if (work is null)
        {
            return ApiError.NotFound().ToHttpResult();
        }

        if (context.Request.CheckIfMatch(AdminRules.ETagOf(work.Revision)) is { } precondition)
        {
            return precondition.ToHttpResult();
        }

        audit.Record(archived ? "work.archived" : "work.restored", "work", key, null);
        await importer.SetWorkPublicationAsync(work, archived, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await importer.EvictCacheAsync(cancellationToken);

        var detail = (await LoadWorkAsync(key, cancellationToken))!.Value;
        context.Response.SetETag(detail.ETag);
        return TypedResults.Ok(detail.Detail);
    }

    public async Task<(AdminComplexDetail Detail, string ETag)?> LoadComplexAsync(string key, CancellationToken cancellationToken)
    {
        var complex = await db.Complexes.AsNoTracking()
            .Include(c => c.Translations)
            .Include(c => c.Images).ThenInclude(i => i.Media)
            .AsSplitQuery()
            .SingleOrDefaultAsync(c => c.Key == key, cancellationToken);
        if (complex is null)
        {
            return null;
        }

        var members = await db.Works.AsNoTracking().Where(w => w.ComplexId == complex.Id).OrderBy(w => w.ComplexPosition)
            .Select(w => new AdminMember(
                w.Key, w.Translations.Where(t => t.Locale == "tr").Select(t => t.Title).FirstOrDefault(), w.ComplexPosition!.Value,
                w.IsActive, w.Latitude != null, w.DeletedAt == null ? WorkQuery.Published : WorkQuery.Archived))
            .ToListAsync(cancellationToken);
        var detail = new AdminComplexDetail(
            complex.Key,
            new VgmName(complex.Translations.SingleOrDefault(t => t.Locale == "tr")?.Name,
                complex.Translations.SingleOrDefault(t => t.Locale == "en")?.Name),
            complex.CityCode, complex.District, complex.Address,
            complex.Location is { } point ? new Location(point.Latitude, point.Longitude) : null, complex.IsActive,
            complex.Images.OrderBy(i => i.Position).Select(i => Image(i.Media, [])).ToArray(),
            members, AdminRules.PublicationOf(complex.DeletedAt), complex.DeletedAt, complex.UpdatedAt);
        return (detail, AdminRules.ETagOf(complex.Revision));
    }

    public async Task<IReadOnlyList<AdminComplexSummary>> ComplexesAsync(IReadOnlyList<short> cities, CancellationToken cancellationToken)
    {
        var complexes = db.Complexes.AsNoTracking();
        if (cities.Count > 0)
        {
            complexes = complexes.Where(c => cities.Contains(c.CityCode));
        }

        return await complexes
            .OrderBy(c => c.Translations.Where(t => t.Locale == "tr").Select(t => t.Name).FirstOrDefault()).ThenBy(c => c.Key)
            .Select(c => new AdminComplexSummary(
                c.Key,
                new VgmName(c.Translations.Where(t => t.Locale == "tr").Select(t => t.Name).FirstOrDefault(),
                    c.Translations.Where(t => t.Locale == "en").Select(t => t.Name).FirstOrDefault()),
                c.CityCode, c.District, c.Works.Count(), c.Latitude != null, c.IsActive,
                c.DeletedAt == null ? WorkQuery.Published : WorkQuery.Archived, c.UpdatedAt))
            .ToListAsync(cancellationToken);
    }

    // create (key null) or replace; members are set separately
    public async Task<IResult> SaveComplexAsync(string? key, AdminComplexInput input, HttpContext context,
        CancellationToken cancellationToken)
    {
        var creating = key is null;
        var targetKey = key ?? input.Key;
        if (!creating && input.Key is not null && input.Key != key)
        {
            return Invalid("invalid_complex", [new VgmIssue("key", "key_mismatch", input.Key)]);
        }

        if (!creating && !context.Request.HasIfMatch())
        {
            return ApiError.PreconditionRequired().ToHttpResult();
        }

        var sources = new List<ImageSource>();
        var complex = new VgmComplex(targetKey, input.Name, input.CityCode, input.District, input.Address, input.IsActive,
            Images(input.Images, "images", sources), Works: null);
        var file = new VgmFile(null, VgmValidator.FormatVersion, [complex], []);
        var errors = (await ValidateAsync(file, "complexes[0].", cancellationToken))
            .Concat(ValidateLocation(input.Location)).Concat(Validate(sources)).ToList();
        if (errors.Count > 0)
        {
            return Invalid("invalid_complex", errors);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var existing = await db.Complexes.FromSql($"SELECT * FROM complexes WHERE key = {targetKey} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (creating && existing is not null)
        {
            return ApiError.Conflict(ErrorCodes.ComplexExists).ToHttpResult();
        }

        if (!creating)
        {
            if (existing is null)
            {
                return ApiError.NotFound().ToHttpResult();
            }

            if (context.Request.CheckIfMatch(AdminRules.ETagOf(existing.Revision)) is { } precondition)
            {
                return precondition.ToHttpResult();
            }
        }

        var resolved = await ResolveImagesAsync(targetKey!, sources, context, cancellationToken);
        if (!resolved.IsSuccess)
        {
            return resolved.Error.ToHttpResult();
        }

        audit.Record(creating ? "complex.created" : "complex.updated", "complex", targetKey!, new Dictionary<string, string?>
        {
            ["images"] = sources.Count.ToString(CultureInfo.InvariantCulture),
        });
        await CommitAsync(() => importer.SaveComplexAsync(complex, PointOf(input.Location), resolved.Value.Media, cancellationToken),
            transaction, resolved.Value.Claimed, cancellationToken);
        if (sources.Any(s => s.Input.Url is not null))
        {
            mirror.Notify();
        }

        var detail = (await LoadComplexAsync(targetKey!, cancellationToken))!.Value;
        context.Response.SetETag(detail.ETag);
        return creating ? TypedResults.Created($"complexes/{targetKey}", detail.Detail) : TypedResults.Ok(detail.Detail);
    }

    public async Task<IResult> SetComplexPublicationAsync(string key, bool archived, HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!context.Request.HasIfMatch())
        {
            return ApiError.PreconditionRequired().ToHttpResult();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var complex = await db.Complexes.FromSql($"SELECT * FROM complexes WHERE key = {key} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (complex is null)
        {
            return ApiError.NotFound().ToHttpResult();
        }

        if (context.Request.CheckIfMatch(AdminRules.ETagOf(complex.Revision)) is { } precondition)
        {
            return precondition.ToHttpResult();
        }

        audit.Record(archived ? "complex.archived" : "complex.restored", "complex", key, null);
        var result = await importer.SetComplexPublicationAsync(complex, archived, cancellationToken);
        if (!result.IsSuccess)
        {
            return result.Error.ToHttpResult();
        }

        await transaction.CommitAsync(cancellationToken);
        await importer.EvictCacheAsync(cancellationToken);

        var detail = (await LoadComplexAsync(key, cancellationToken))!.Value;
        context.Response.SetETag(detail.ETag);
        return TypedResults.Ok(detail.Detail);
    }

    public async Task<IResult> SetComplexWorksAsync(string key, IReadOnlyList<string> workKeys, HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!context.Request.HasIfMatch())
        {
            return ApiError.PreconditionRequired().ToHttpResult();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var complex = await db.Complexes.FromSql($"SELECT * FROM complexes WHERE key = {key} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (complex is null)
        {
            return ApiError.NotFound().ToHttpResult();
        }

        if (context.Request.CheckIfMatch(AdminRules.ETagOf(complex.Revision)) is { } precondition)
        {
            return precondition.ToHttpResult();
        }

        audit.Record("complex.works-updated", "complex", key, new Dictionary<string, string?> { ["works"] = string.Join(',', workKeys) });
        var result = await importer.SetMembersAsync(complex, workKeys, cancellationToken);
        if (!result.IsSuccess)
        {
            return result.Error.ToHttpResult();
        }

        await transaction.CommitAsync(cancellationToken);
        await importer.EvictCacheAsync(cancellationToken);

        var detail = (await LoadComplexAsync(key, cancellationToken))!.Value;
        context.Response.SetETag(detail.ETag);
        return TypedResults.Ok(detail.Detail);
    }

    // ticket for a direct upload to storage; the save that uses it claims the photo
    public async Task<IResult> RequestUploadAsync(PhotoUploadRequest request, HttpContext context, CancellationToken cancellationToken)
    {
        var ticket = await uploads.CreateTicketAsync(context.User.RequiredUserId(), "catalog", request, cancellationToken);
        return ticket.IsSuccess ? TypedResults.Created((string?)null, ticket.Value) : ticket.Error.ToHttpResult();
    }

    public async Task<CatalogAdminSummary> SummaryAsync(CancellationToken cancellationToken)
    {
        var works = await db.Works.AsNoTracking().Select(w => new
        {
            w.IsActive,
            w.DeletedAt,
            w.Category,
            w.Period,
            w.CityCode,
            City = w.City.Name,
            HasLocation = w.Latitude != null,
            HasImages = w.Images.Any(),
        }).ToListAsync(cancellationToken);
        var published = works.Where(w => w.DeletedAt == null).ToList();
        var categories = published.GroupBy(w => w.Category).ToDictionary(g => g.Key, g => g.Count());
        var periods = published.GroupBy(w => w.Period).ToDictionary(g => g.Key, g => g.Count());
        var version = await db.Meta.AsNoTracking().Select(m => m.Version).SingleAsync(cancellationToken);
        var pending = await db.MediaAssets.CountAsync(m => m.StorageKey == null, cancellationToken);
        return new CatalogAdminSummary(
            works.Count, published.Count, works.Count - published.Count, published.Count(w => !w.IsActive),
            published.Count(w => !w.HasImages), published.Count(w => !w.HasLocation),
            await db.Complexes.CountAsync(c => c.DeletedAt == null, cancellationToken), pending,
            WorkCategory.All.ToDictionary(c => c, c => categories.GetValueOrDefault(c)),
            WorkPeriod.All.ToDictionary(p => p, p => periods.GetValueOrDefault(p)),
            published.GroupBy(w => (w.CityCode, w.City)).OrderBy(g => g.Key.CityCode)
                .Select(g => new CityFacet(g.Key.CityCode, g.Key.City, g.Count())).ToArray(),
            version.ToString(CultureInfo.InvariantCulture));
    }

    // a panel image and the placeholder src it gets in the importer's file shape
    private sealed record ImageSource(string Src, string Path, AdminImageInput Input);

    private sealed record ResolvedImages(IReadOnlyDictionary<string, long> Media, IReadOnlyList<StoredPhoto> Claimed);

    private static List<VgmImage>? Images(IReadOnlyList<AdminImageInput>? inputs, string path, List<ImageSource> sources) =>
        inputs?.Select((input, i) =>
        {
            var src = $"images/{sources.Count}.jpg";
            sources.Add(new ImageSource(src, $"{path}[{i}]", input));
            return new VgmImage(src, input.Translations);
        }).ToList();

    private static VgmWorkText? FileText(AdminWorkText? text) => text is null
        ? null
        : new VgmWorkText(text.Slug, text.Title, text.Nickname, text.ShortDescriptionTitle, text.ShortDescription);

    private static WorkStaffText? StaffText(AdminWorkText? text) =>
        text is null ? null : new WorkStaffText(text.History, text.ConstructionDate, text.Founder, text.Architect);

    private static GeoPoint? PointOf(AdminLocation? location) =>
        location is { Latitude: { } latitude, Longitude: { } longitude } ? new GeoPoint(latitude, longitude) : null;

    // the importer's file rules (images resolved later, after the lock)
    private async Task<IReadOnlyList<VgmIssue>> ValidateAsync(VgmFile file, string prefix, CancellationToken cancellationToken)
    {
        var cities = (await db.Cities.Select(c => c.Code).ToListAsync(cancellationToken)).ToHashSet();
        var validation = VgmValidator.Validate(file, cities, _ => true);
        var errors = validation.Errors.Count > 0 ? validation.Errors : await importer.CheckAgainstDatabaseAsync(file, cancellationToken);
        return errors.Select(e => e with { Path = e.Path.StartsWith(prefix, StringComparison.Ordinal) ? e.Path[prefix.Length..] : e.Path })
            .ToArray();
    }

    private static IEnumerable<VgmIssue> ValidateDetails(AdminWorkInput input)
    {
        if (input.Category is not null && !WorkCategory.All.Contains(input.Category))
        {
            yield return new VgmIssue("category", "invalid_category", input.Category);
        }

        if (input.Period is not null && !WorkPeriod.All.Contains(input.Period))
        {
            yield return new VgmIssue("period", "invalid_period", input.Period);
        }

        if (input.District?.Trim().Length > CatalogLimits.District)
        {
            yield return new VgmIssue("district", "too_long");
        }

        foreach (var issue in ValidateLocation(input.Location))
        {
            yield return issue;
        }

        foreach (var (language, text) in new[] { ("tr", input.Translations?.Tr), ("en", input.Translations?.En) })
        {
            var path = $"translations.{language}";
            if (text?.History?.Trim().Length > CatalogLimits.LongText)
            {
                yield return new VgmIssue($"{path}.history", "too_long");
            }

            foreach (var (field, value) in new[] { ("constructionDate", text?.ConstructionDate), ("founder", text?.Founder), ("architect", text?.Architect) })
            {
                if (value?.Trim().Length > CatalogLimits.Text)
                {
                    yield return new VgmIssue($"{path}.{field}", "too_long");
                }
            }
        }
    }

    private static IEnumerable<VgmIssue> ValidateLocation(AdminLocation? location)
    {
        if (location is null)
        {
            yield break;
        }

        if (PointOf(location) is not { } point)
        {
            yield return new VgmIssue("location", "incomplete_location");
        }
        else if (!point.IsValid || !CatalogRegion.Contains(point.Latitude, point.Longitude))
        {
            yield return new VgmIssue("location", "location_out_of_range");
        }
    }

    private static IEnumerable<VgmIssue> Validate(IReadOnlyList<ImageSource> sources)
    {
        if (sources.Count > AdminRules.MaxImages)
        {
            yield return new VgmIssue("images", "too_many_images");
        }

        foreach (var (_, path, input) in sources)
        {
            var given = new[] { input.Sha256, input.UploadId, input.Url }.Count(v => v is not null);
            if (given != 1)
            {
                yield return new VgmIssue(path, "one_image_source");
                continue;
            }

            if (input.Sha256 is { } sha && !VgmValidator.Sha256Pattern().IsMatch(sha))
            {
                yield return new VgmIssue($"{path}.sha256", "invalid_sha256");
            }

            if (input.UploadId is { Length: 0 or > AdminRules.MaxUploadIdLength })
            {
                yield return new VgmIssue($"{path}.uploadId", "invalid_upload_id");
            }

            if (input.Url is { } url && !SafeHttp.IsAllowedUrl(url))
            {
                yield return new VgmIssue($"{path}.url", "invalid_url");
            }

            var attribution = input.Attribution;
            // a file from elsewhere needs a licence credit
            if (input.Url is not null && (string.IsNullOrWhiteSpace(attribution?.Author) || string.IsNullOrWhiteSpace(attribution.License)))
            {
                yield return new VgmIssue($"{path}.attribution", "attribution_required");
            }

            foreach (var (field, value) in new[] { ("author", attribution?.Author), ("license", attribution?.License), ("sourceName", attribution?.SourceName) })
            {
                if (value?.Trim().Length > CatalogLimits.Text)
                {
                    yield return new VgmIssue($"{path}.attribution.{field}", "too_long");
                }
            }

            if (attribution?.SourceUrl is { } source &&
                (source.Length > CatalogLimits.Url || !Uri.TryCreate(source, UriKind.Absolute, out var uri) ||
                 uri.Scheme is not ("https" or "http")))
            {
                yield return new VgmIssue($"{path}.attribution.sourceUrl", "invalid_url");
            }
        }
    }

    // media row per panel image: an uploaded file by hash, a claimed upload ticket (new row), or a URL (existing
    // row for that origin, else a new one waiting for the mirror; a failed mirror is retried). Attribution is
    // written to the row. Runs inside the caller's transaction; claimed objects are returned for cleanup
    private async Task<Result<ResolvedImages>> ResolveImagesAsync(string ownerKey, IReadOnlyList<ImageSource> sources,
        HttpContext context, CancellationToken cancellationToken)
    {
        var bySrc = new Dictionary<string, MediaAsset>(StringComparer.Ordinal);

        var hashes = sources.Where(s => s.Input.Sha256 is not null).ToList();
        if (hashes.Count > 0)
        {
            var keys = hashes.Select(s => ImportImageStore.StorageKeyOf(s.Input.Sha256!)).Distinct().ToList();
            var stored = await db.MediaAssets.Where(m => keys.Contains(m.StorageKey!)).ToDictionaryAsync(m => m.StorageKey!, cancellationToken);
            if (hashes.Any(s => !stored.ContainsKey(ImportImageStore.StorageKeyOf(s.Input.Sha256!))))
            {
                return ApiError.Conflict(ErrorCodes.ImportImagesMissing);
            }

            foreach (var source in hashes)
            {
                bySrc[source.Src] = stored[ImportImageStore.StorageKeyOf(source.Input.Sha256!)];
            }
        }

        var claimed = new List<StoredPhoto>();
        var tickets = sources.Where(s => s.Input.UploadId is not null).ToList();
        foreach (var batch in tickets.Chunk(PhotoRules.MaxPhotos))
        {
            var result = await uploads.ClaimAsync(context.User.RequiredUserId(),
                batch.Select(s => new PhotoAttachment(s.Input.UploadId, null)).ToList(), storage.Value.Buckets.Public,
                $"catalog/{ownerKey}", ErrorCodes.InvalidUpload, cancellationToken);
            if (!result.IsSuccess)
            {
                await uploads.DeleteAsync(storage.Value.Buckets.Public, claimed, cancellationToken);
                return result.Error!;
            }

            foreach (var (source, photo) in batch.Zip(result.Value))
            {
                var media = new MediaAsset
                {
                    StorageKey = photo.Key,
                    ThumbnailKey = photo.ThumbnailKey,
                    MimeType = "image/jpeg",
                    SizeBytes = (int)photo.ByteSize,
                };
                db.MediaAssets.Add(media);
                bySrc[source.Src] = media;
                claimed.Add(photo);
            }
        }

        var linked = sources.Where(s => s.Input.Url is not null).ToList();
        if (linked.Count > 0)
        {
            var origins = linked.Select(s => s.Input.Url!).Distinct(StringComparer.Ordinal).ToList();
            var known = await db.MediaAssets.Where(m => origins.Contains(m.OriginUrl!)).ToDictionaryAsync(m => m.OriginUrl!, cancellationToken);
            foreach (var source in linked)
            {
                if (!known.TryGetValue(source.Input.Url!, out var media))
                {
                    media = new MediaAsset { OriginUrl = source.Input.Url };
                    db.MediaAssets.Add(media);
                    known[media.OriginUrl!] = media;
                }
                else if (!media.IsStored && media.MirrorAttempts >= MediaAsset.MaxMirrorAttempts)
                {
                    (media.MirrorAttempts, media.MirrorRetryAt, media.MirrorError) = (0, null, null);
                }

                bySrc[source.Src] = media;
            }
        }

        foreach (var source in sources.Where(s => s.Input.Attribution is not null))
        {
            var (media, credit) = (bySrc[source.Src], source.Input.Attribution!);
            media.Author = VgmText.Clean(credit.Author);
            media.License = VgmText.Clean(credit.License);
            media.SourceName = VgmText.Clean(credit.SourceName);
            media.SourceUrl = VgmText.Clean(credit.SourceUrl);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            await uploads.DeleteAsync(storage.Value.Buckets.Public, claimed, cancellationToken);
            throw;
        }

        return new ResolvedImages(bySrc.ToDictionary(m => m.Key, m => m.Value.Id, StringComparer.Ordinal), claimed);
    }

    // commits the save; on failure the transaction rolls back and claimed upload objects are deleted
    private async Task CommitAsync(Func<Task<string>> save, IDbContextTransaction transaction,
        IReadOnlyList<StoredPhoto> claimed, CancellationToken cancellationToken)
    {
        try
        {
            await save();
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await uploads.DeleteAsync(storage.Value.Buckets.Public, claimed, cancellationToken);
            throw;
        }

        await importer.EvictCacheAsync(cancellationToken);
    }

    private AdminImage Image(MediaAsset media, IEnumerable<(string Locale, string? AltText, string? Title)> texts)
    {
        var all = texts.ToArray();
        VgmImageText? Text(string locale) =>
            all.Where(t => t.Locale == locale).Select(t => new VgmImageText(t.AltText, t.Title)).SingleOrDefault();
        var reference = MediaRef.Of(media);
        return new AdminImage(media.Id, ImportImageStore.Sha256Of(media.StorageKey), media.OriginUrl, urls.Url(reference),
            urls.ThumbnailUrl(reference), media.IsStored, media.MirrorError, MediaUrls.AttributionOf(reference),
            new VgmLanguages<VgmImageText>(Text("tr"), Text("en")));
    }

    private static IResult Invalid(string code, IReadOnlyList<VgmIssue> errors) =>
        TypedResults.BadRequest(new AdminValidationError(code, errors));
}
