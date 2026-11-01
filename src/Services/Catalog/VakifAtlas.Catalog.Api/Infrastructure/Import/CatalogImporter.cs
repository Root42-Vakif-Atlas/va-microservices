using System.Globalization;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.EntityFrameworkCore;
using VakifAtlas.Catalog.Api.Infrastructure.Persistence;
using VakifAtlas.Catalog.Domain;
using VakifAtlas.Catalog.Domain.Complexes;
using VakifAtlas.Catalog.Domain.Works;
using VakifAtlas.Contracts;
using VakifAtlas.Messaging;
using VakifAtlas.SharedKernel;

namespace VakifAtlas.Catalog.Api.Infrastructure.Import;

public sealed record ImportResult(int CreatedComplexes, int UpdatedComplexes, int CreatedWorks, int UpdatedWorks, string CatalogVersion);

public sealed record WorkStaffText(string? History, string? ConstructionDate, string? Founder, string? Architect);

// fields only the staff panel sets (the bulk file has none of them). Category null = follow the tr title
public sealed record WorkDetails(
    string? Category, string Period, bool IsUnescoWorldHeritage, string? District, GeoPoint? Location,
    VgmLanguages<WorkStaffText>? Texts);

// the single write path of the catalog (file import, seeder, staff panel, image mirror). Every write bumps the
// catalog version and publishes events in the same transaction (outbox), and the public cache is evicted after
// commit. Rules for a file: complexes and works are upserted by key, texts, images and parts are replaced
// position by position and complex membership follows the file; what staff own is kept: location (filled
// from the file only while empty), district (filled while empty), period, UNESCO flag, history texts, a
// corrected category, and deleted_at
public sealed class CatalogImporter(
    CatalogDbContext db,
    IEventPublisher events,
    IOutputCacheStore cache,
    TimeProvider clock,
    ILogger<CatalogImporter> logger)
{
    public static readonly string[] Locales = ["tr", "en"];

    // mediaBySrc: every image src of the file → media_assets.id. Runs in its own transaction unless the caller
    // has one; evictCache false: the caller evicts after committing
    public async Task<ImportResult> ImportFileAsync(VgmFile file, IReadOnlyDictionary<string, long> mediaBySrc,
        CancellationToken cancellationToken, bool evictCache = true)
    {
        var complexSources = file.Complexes ?? [];
        var workSources = file.Works ?? [];
        var complexKeys = complexSources.Select(c => c.Key!).ToList();
        var workKeys = workSources.Select(w => w.Key!).ToList();

        await using var transaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;

        var cities = await db.Cities.AsNoTracking().ToDictionaryAsync(c => c.Code, c => c.Name, cancellationToken);
        var complexes = await LoadComplexesAsync(complexKeys, cancellationToken);
        var works = await LoadWorksAsync(workKeys, cancellationToken);
        var result = (CreatedComplexes: complexKeys.Count(k => !complexes.ContainsKey(k)),
            CreatedWorks: workKeys.Count(k => !works.ContainsKey(k)));

        foreach (var source in complexSources)
        {
            var complex = complexes.GetValueOrDefault(source.Key!) ?? Add(new Complex { Key = source.Key! }, complexes);
            ApplyComplex(complex, source, mediaBySrc);
            // a file location only fills an empty one: staff corrections win
            complex.Location ??= source.Location?.Point;
        }

        // members take their complex's district
        var districts = complexSources.SelectMany(c => (c.Works ?? []).Select(w => (Work: w, District: c.District!.Trim())))
            .ToDictionary(m => m.Work, m => m.District, StringComparer.Ordinal);
        foreach (var source in workSources)
        {
            var work = works.GetValueOrDefault(source.Key!) ?? Add(new Work { Key = source.Key! }, works);
            ApplyWork(work, source, details: null, mediaBySrc);
            if (string.IsNullOrEmpty(work.District))
            {
                work.District = districts.GetValueOrDefault(work.Key) ??
                                AddressParser.District(work.Address, cities.GetValueOrDefault(work.CityCode, string.Empty));
            }

            work.Location ??= source.Location?.Point;
        }

        await AssignSlugsAsync(workSources.Select(s => (works[s.Key!], s.Translations?.En?.Slug)), cancellationToken);
        var moved = await AssignMembershipAsync(complexSources, complexes, works, cancellationToken);

        var version = await PublishChangesAsync(workKeys.Concat(moved).Distinct().ToList(), complexKeys, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await TouchAsync(complexKeys, workKeys, cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        if (evictCache)
        {
            await EvictCacheAsync(cancellationToken);
        }

        logger.LogInformation("Imported {Complexes} complexes and {Works} works; catalog version {Version}", complexKeys.Count,
            workKeys.Count, version);
        return new ImportResult(result.CreatedComplexes, complexKeys.Count - result.CreatedComplexes, result.CreatedWorks,
            workKeys.Count - result.CreatedWorks, version);
    }

    // staff save (create or replace) of one work; membership is untouched. Runs in the caller's transaction
    public async Task<string> SaveWorkAsync(VgmWork source, WorkDetails details, IReadOnlyDictionary<string, long> mediaBySrc,
        CancellationToken cancellationToken)
    {
        RequireTransaction();
        var works = await LoadWorksAsync([source.Key!], cancellationToken);
        var work = works.GetValueOrDefault(source.Key!) ?? Add(new Work { Key = source.Key! }, works);
        ApplyWork(work, source, details, mediaBySrc);
        await AssignSlugsAsync([(work, source.Translations?.En?.Slug)], cancellationToken);

        var version = await PublishChangesAsync([work.Key], [], cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await TouchAsync([], [work.Key], cancellationToken);
        return version;
    }

    // staff save (create or replace) of one complex; members are set separately. Runs in the caller's transaction
    public async Task<string> SaveComplexAsync(VgmComplex source, GeoPoint? location, IReadOnlyDictionary<string, long> mediaBySrc,
        CancellationToken cancellationToken)
    {
        RequireTransaction();
        var complexes = await LoadComplexesAsync([source.Key!], cancellationToken);
        var complex = complexes.GetValueOrDefault(source.Key!) ?? Add(new Complex { Key = source.Key! }, complexes);
        ApplyComplex(complex, source, mediaBySrc);
        complex.Location = location;

        // its works' map marker and complex name change with it
        var members = complex.Id == 0
            ? []
            : await db.Works.Where(w => w.ComplexId == complex.Id).Select(w => w.Key).ToListAsync(cancellationToken);
        var version = await PublishChangesAsync(members, [complex.Key], cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await TouchAsync([complex.Key], [], cancellationToken);
        return version;
    }

    // replaces a complex's works with the ordered list (position = index); works left out become standalone.
    // Works must be in the complex's city. Runs in the caller's transaction
    public async Task<Result<string>> SetMembersAsync(Complex complex, IReadOnlyList<string> workKeys,
        CancellationToken cancellationToken)
    {
        RequireTransaction();
        if (workKeys.Distinct(StringComparer.Ordinal).Count() != workKeys.Count)
        {
            return ApiError.BadRequest(ErrorCodes.InvalidComplexMembers);
        }

        var members = await db.Works.Where(w => workKeys.Contains(w.Key)).ToDictionaryAsync(w => w.Key, cancellationToken);
        if (members.Count != workKeys.Count || members.Values.Any(w => w.CityCode != complex.CityCode))
        {
            return ApiError.BadRequest(ErrorCodes.InvalidComplexMembers);
        }

        var previous = await db.Works.Where(w => w.ComplexId == complex.Id && !workKeys.Contains(w.Key)).ToListAsync(cancellationToken);
        foreach (var work in members.Values.Concat(previous))
        {
            work.ComplexId = null;
            work.ComplexPosition = null;
        }

        // positions are unique per complex: clear first, then assign
        await db.SaveChangesAsync(cancellationToken);
        for (var position = 0; position < workKeys.Count; position++)
        {
            members[workKeys[position]].ComplexId = complex.Id;
            members[workKeys[position]].ComplexPosition = position;
        }

        var version = await PublishChangesAsync(workKeys.Concat(previous.Select(w => w.Key)).ToList(), [complex.Key],
            cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await TouchAsync([complex.Key], [], cancellationToken);
        return version;
    }

    public async Task<string> SetWorkPublicationAsync(Work work, bool archived, CancellationToken cancellationToken)
    {
        RequireTransaction();
        var now = clock.GetUtcNow();
        work.DeletedAt = archived ? work.DeletedAt ?? now : null;
        var version = await PublishChangesAsync([work.Key], [], cancellationToken);
        events.Publish(new WorkPublicationChanged(Guid.NewGuid(), now, work.Key, archived ? "archived" : "published", version));
        await db.SaveChangesAsync(cancellationToken);
        return version;
    }

    // a complex with published works cannot be archived (move or archive them first)
    public async Task<Result<string>> SetComplexPublicationAsync(Complex complex, bool archived, CancellationToken cancellationToken)
    {
        RequireTransaction();
        if (archived && await db.Works.AnyAsync(w => w.ComplexId == complex.Id && w.DeletedAt == null, cancellationToken))
        {
            return ApiError.Conflict(ErrorCodes.InvalidComplexMembers);
        }

        complex.DeletedAt = archived ? complex.DeletedAt ?? clock.GetUtcNow() : null;
        var version = await PublishChangesAsync([], [complex.Key], cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return version;
    }

    // bumps the catalog version (row lock until commit, so versions follow commit order) and publishes the
    // events of this change; both are flushed by the caller's SaveChanges in the same transaction
    public async Task<string> PublishChangesAsync(IReadOnlyCollection<string> workKeys, IReadOnlyCollection<string> complexKeys,
        CancellationToken cancellationToken)
    {
        RequireTransaction();
        var rows = await db.Database.QueryAsync(
            $"UPDATE catalog_meta SET version = version + 1, updated_at = now() WHERE id = {CatalogMeta.SingletonId} RETURNING version",
            [], r => r.GetInt64(0), cancellationToken);
        var version = rows.Single().ToString(CultureInfo.InvariantCulture);
        var now = clock.GetUtcNow();
        events.Publish(new CatalogVersionChanged(Guid.NewGuid(), now, version));
        foreach (var key in workKeys)
        {
            events.Publish(new WorkUpserted(Guid.NewGuid(), now, key, version));
        }

        foreach (var key in complexKeys)
        {
            events.Publish(new ComplexUpserted(Guid.NewGuid(), now, key, version));
        }

        return version;
    }

    public Task EvictCacheAsync(CancellationToken cancellationToken) =>
        cache.EvictByTagAsync(CatalogCaching.Tag, cancellationToken).AsTask();

    // file checks that need the database: en slugs used by other works, and a complex's city (part of the
    // key the works FK targets, so it cannot change)
    public async Task<IReadOnlyList<VgmIssue>> CheckAgainstDatabaseAsync(VgmFile file, CancellationToken cancellationToken)
    {
        var issues = new List<VgmIssue>();
        var works = file.Works ?? [];
        var keys = works.Select(w => w.Key).OfType<string>().ToList();
        var slugs = works.Select((w, i) => (Index: i, Slug: VgmText.Clean(w.Translations?.En?.Slug)))
            .Where(s => s.Slug is not null).ToList();
        if (slugs.Count > 0)
        {
            var values = slugs.Select(s => s.Slug!).ToList();
            var taken = (await db.Works.Where(w => !keys.Contains(w.Key))
                    .SelectMany(w => w.Translations)
                    .Where(t => t.Locale == "en" && t.Slug != null && values.Contains(t.Slug))
                    .Select(t => t.Slug!).ToListAsync(cancellationToken))
                .ToHashSet(StringComparer.Ordinal);
            issues.AddRange(slugs.Where(s => taken.Contains(s.Slug!))
                .Select(s => new VgmIssue($"works[{s.Index}].translations.en.slug", "slug_taken", s.Slug)));
        }

        var complexes = file.Complexes ?? [];
        var complexKeys = complexes.Select(c => c.Key).OfType<string>().ToList();
        var cities = await db.Complexes.Where(c => complexKeys.Contains(c.Key))
            .ToDictionaryAsync(c => c.Key, c => c.CityCode, cancellationToken);
        for (var i = 0; i < complexes.Count; i++)
        {
            if (complexes[i].Key is { } key && cities.TryGetValue(key, out var city) && city != complexes[i].CityCode)
            {
                issues.Add(new VgmIssue($"complexes[{i}].cityCode", "complex_city_changed", key));
            }
        }

        return issues;
    }

    private void RequireTransaction()
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Catalog writes must run in a transaction.");
        }
    }

    private Work Add(Work work, Dictionary<string, Work> byKey)
    {
        db.Works.Add(work);
        return byKey[work.Key] = work;
    }

    private Complex Add(Complex complex, Dictionary<string, Complex> byKey)
    {
        db.Complexes.Add(complex);
        return byKey[complex.Key] = complex;
    }

    private Task<Dictionary<string, Complex>> LoadComplexesAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken) =>
        db.Complexes.Include(c => c.Translations).Include(c => c.Images)
            .AsSplitQuery().Where(c => keys.Contains(c.Key)).ToDictionaryAsync(c => c.Key, cancellationToken);

    private Task<Dictionary<string, Work>> LoadWorksAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken) =>
        db.Works
            .Include(w => w.Translations)
            .Include(w => w.Images).ThenInclude(i => i.Translations)
            .Include(w => w.Parts).ThenInclude(p => p.Translations)
            .Include(w => w.Parts).ThenInclude(p => p.Images).ThenInclude(i => i.Translations)
            .AsSplitQuery().Where(w => keys.Contains(w.Key)).ToDictionaryAsync(w => w.Key, cancellationToken);

    // a file's complexes get exactly their listed works; works in the file that no listed complex names become
    // standalone, as do former works left out of a complex's list. Positions are unique per complex, so the
    // affected rows are cleared and saved before they are assigned. Returns the keys of works moved out
    private async Task<IReadOnlyList<string>> AssignMembershipAsync(List<VgmComplex> sources, Dictionary<string, Complex> complexes,
        Dictionary<string, Work> works, CancellationToken cancellationToken)
    {
        var complexIds = sources.Select(s => complexes[s.Key!].Id).Where(id => id != 0).ToList();
        var workKeys = works.Keys.ToList();
        var outside = await db.Works
            .Where(w => w.ComplexId != null && complexIds.Contains(w.ComplexId.Value) && !workKeys.Contains(w.Key))
            .ToListAsync(cancellationToken);
        foreach (var work in works.Values.Concat(outside))
        {
            work.ComplexId = null;
            work.ComplexPosition = null;
        }

        await db.SaveChangesAsync(cancellationToken);
        foreach (var source in sources)
        {
            var complex = complexes[source.Key!];
            var members = source.Works ?? [];
            for (var position = 0; position < members.Count; position++)
            {
                works[members[position]].ComplexId = complex.Id;
                works[members[position]].ComplexPosition = position;
            }
        }

        return outside.Select(w => w.Key).ToArray();
    }

    // child rows do not update their parent row, so the parent is touched: updated_at and revision (staff ETag)
    private async Task TouchAsync(IReadOnlyList<string> complexKeys, IReadOnlyList<string> workKeys, CancellationToken cancellationToken)
    {
        if (complexKeys.Count > 0)
        {
            await db.Complexes.Where(c => complexKeys.Contains(c.Key))
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.IsActive, c => c.IsActive), cancellationToken);
        }

        if (workKeys.Count > 0)
        {
            await db.Works.Where(w => workKeys.Contains(w.Key))
                .ExecuteUpdateAsync(s => s.SetProperty(w => w.IsActive, w => w.IsActive), cancellationToken);
        }
    }

    // en slug of every work that has an en row: the explicit one (uniqueness checked before), else the current
    // one (en URLs stay stable), else generated from the title and suffixed on collision
    private async Task AssignSlugsAsync(IEnumerable<(Work Work, string? Explicit)> items, CancellationToken cancellationToken)
    {
        var list = items.Select(i => (i.Work, Explicit: VgmText.Clean(i.Explicit),
            Row: i.Work.Translations.SingleOrDefault(t => t.Locale == "en"))).ToList();
        var keys = list.Select(i => i.Work.Key).ToList();
        var taken = (await db.Works.Where(w => !keys.Contains(w.Key))
                .SelectMany(w => w.Translations)
                .Where(t => t.Locale == "en" && t.Slug != null)
                .Select(t => t.Slug!).ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var item in list.Where(i => i.Row is not null && i.Explicit is not null))
        {
            item.Row!.Slug = item.Explicit;
            taken.Add(item.Explicit!);
        }

        foreach (var item in list.Where(i => i.Row is not null && i.Explicit is null))
        {
            var current = item.Row!.Slug;
            if (current is null || taken.Contains(current))
            {
                var title = item.Row.Title ?? item.Work.Translations.Single(t => t.Locale == "tr").Title!;
                current = Unique(CatalogKey.From(title), item.Work.Key, taken);
            }

            item.Row.Slug = current;
            taken.Add(current);
        }
    }

    private static string Unique(string slug, string fallback, HashSet<string> taken)
    {
        var root = slug.Length > 0 ? slug : fallback;
        var candidate = root;
        for (var n = 2; taken.Contains(candidate); n++)
        {
            var suffix = $"-{n}";
            candidate = (root.Length + suffix.Length > CatalogKey.MaxLength ? root[..(CatalogKey.MaxLength - suffix.Length)] : root) + suffix;
        }

        return candidate;
    }

    private static void ApplyComplex(Complex complex, VgmComplex source, IReadOnlyDictionary<string, long> mediaBySrc)
    {
        complex.CityCode = (short)source.CityCode;
        complex.District = source.District!.Trim();
        complex.Address = VgmText.Clean(source.Address);
        complex.IsActive = source.IsActive ?? true;
        SetName(complex, "tr", source.Name!.Tr);
        SetName(complex, "en", source.Name.En);

        var images = source.Images ?? [];
        SyncPositions(complex.Images, images.Count, i => i.Position, p => new ComplexImage { Position = (short)p },
            (image, p) => image.MediaId = mediaBySrc[images[p].Src!]);
    }

    private static void SetName(Complex complex, string locale, string? name)
    {
        var row = complex.Translations.SingleOrDefault(t => t.Locale == locale);
        if (VgmText.Clean(name) is not { } value)
        {
            if (row is not null)
            {
                complex.Translations.Remove(row);
            }

            return;
        }

        if (row is null)
        {
            complex.Translations.Add(new ComplexTranslation { Locale = locale, Name = value });
        }
        else
        {
            row.Name = value;
        }
    }

    // details null (file): staff-owned fields are kept
    private static void ApplyWork(Work work, VgmWork source, WorkDetails? details, IReadOnlyDictionary<string, long> mediaBySrc)
    {
        work.CityCode = (short)source.CityCode;
        work.Address = VgmText.Clean(source.Address);
        work.IsActive = source.IsActive ?? true;
        if (details is not null)
        {
            work.CategoryOverridden = details.Category is not null;
            work.Period = details.Period;
            work.IsUnescoWorldHeritage = details.IsUnescoWorldHeritage;
            work.District = VgmText.Clean(details.District);
            work.Location = details.Location;
        }

        ApplyTexts(work, source.Translations, details);
        if (!work.CategoryOverridden)
        {
            work.Category = WorkCategory.Of(work.Translations.Single(t => t.Locale == "tr").Title);
        }
        else if (details?.Category is { } category)
        {
            work.Category = category;
        }

        var images = source.Images ?? [];
        SyncPositions(work.Images, images.Count, i => i.Position, p => new WorkImage { Position = p }, (image, p) =>
        {
            image.MediaId = mediaBySrc[images[p].Src!];
            SyncLocales(image.Translations, images[p].Translations, t => t.Locale, VgmText.HasContent,
                locale => new WorkImageTranslation { Locale = locale }, ApplyImageText);
        });

        var parts = source.Parts ?? [];
        SyncPositions(work.Parts, parts.Count, p => p.Position, p => new WorkPart { Position = p }, (part, p) =>
        {
            SyncLocales(part.Translations, parts[p].Translations, t => t.Locale, VgmText.HasContent,
                locale => new WorkPartTranslation { Locale = locale },
                (row, text) =>
                {
                    row.Title = VgmText.Clean(text.Title);
                    row.Description = VgmText.Clean(text.Description);
                });

            var partImages = parts[p].Images ?? [];
            SyncPositions(part.Images, partImages.Count, i => i.Position, i => new WorkPartImage { Position = i }, (image, i) =>
            {
                image.MediaId = mediaBySrc[partImages[i].Src!];
                SyncLocales(image.Translations, partImages[i].Translations, t => t.Locale, VgmText.HasContent,
                    locale => new WorkPartImageTranslation { Locale = locale }, ApplyImageText);
            });
        });
    }

    // file texts are always replaced; staff texts only when details are given. An en row without any text left
    // is removed (readers fall back to tr); the slug is assigned afterwards
    private static void ApplyTexts(Work work, VgmLanguages<VgmWorkText>? texts, WorkDetails? details)
    {
        foreach (var locale in Locales)
        {
            var text = locale == "tr" ? texts?.Tr : texts?.En;
            var staff = details is null ? null : locale == "tr" ? details.Texts?.Tr : details.Texts?.En;
            var row = work.Translations.SingleOrDefault(t => t.Locale == locale);
            if (row is null)
            {
                if (!(text is not null && VgmText.HasContent(text)) && !HasContent(staff))
                {
                    continue;
                }

                row = new WorkTranslation { Locale = locale };
                work.Translations.Add(row);
            }

            row.Title = VgmText.Clean(text?.Title);
            row.Nickname = VgmText.Clean(text?.Nickname);
            row.ShortDescriptionTitle = VgmText.Clean(text?.ShortDescriptionTitle);
            row.ShortDescription = VgmText.Clean(text?.ShortDescription);
            if (details is not null)
            {
                row.History = VgmText.Clean(staff?.History);
                row.ConstructionDate = VgmText.Clean(staff?.ConstructionDate);
                row.Founder = VgmText.Clean(staff?.Founder);
                row.Architect = VgmText.Clean(staff?.Architect);
            }

            if (locale != "tr" && row is
                {
                    Title: null, Nickname: null, ShortDescriptionTitle: null, ShortDescription: null,
                    History: null, ConstructionDate: null, Founder: null, Architect: null
                })
            {
                work.Translations.Remove(row);
            }
            else if (locale == "tr")
            {
                row.Slug = null; // the tr URL is the key
            }
        }
    }

    private static bool HasContent(WorkStaffText? text) =>
        text is not null && (VgmText.Clean(text.History) is not null || VgmText.Clean(text.ConstructionDate) is not null ||
                             VgmText.Clean(text.Founder) is not null || VgmText.Clean(text.Architect) is not null);

    private static void ApplyImageText(WorkImageTranslation row, VgmImageText text)
    {
        row.AltText = VgmText.Clean(text.AltText);
        row.Title = VgmText.Clean(text.Title);
    }

    private static void ApplyImageText(WorkPartImageTranslation row, VgmImageText text)
    {
        row.AltText = VgmText.Clean(text.AltText);
        row.Title = VgmText.Clean(text.Title);
    }

    // rows keep their position (and id), so (owner, position) uniqueness never collides mid-save; extra rows are removed
    private static void SyncPositions<TRow>(List<TRow> rows, int count, Func<TRow, int> positionOf, Func<int, TRow> create,
        Action<TRow, int> apply)
    {
        rows.RemoveAll(r => positionOf(r) >= count);
        for (var position = 0; position < count; position++)
        {
            var row = rows.SingleOrDefault(r => positionOf(r) == position);
            if (row is null)
            {
                row = create(position);
                rows.Add(row);
            }

            apply(row, position);
        }
    }

    // one row per locale; a locale without content is removed (readers fall back to tr)
    private static void SyncLocales<TRow, TText>(List<TRow> rows, VgmLanguages<TText>? texts, Func<TRow, string> localeOf,
        Func<TText, bool> hasContent, Func<string, TRow> create, Action<TRow, TText> apply)
        where TText : class
    {
        foreach (var locale in Locales)
        {
            var text = locale == "tr" ? texts?.Tr : texts?.En;
            var row = rows.SingleOrDefault(r => localeOf(r) == locale);
            if (text is null || !hasContent(text))
            {
                if (row is not null)
                {
                    rows.Remove(row);
                }

                continue;
            }

            if (row is null)
            {
                row = create(locale);
                rows.Add(row);
            }

            apply(row, text);
        }
    }
}
