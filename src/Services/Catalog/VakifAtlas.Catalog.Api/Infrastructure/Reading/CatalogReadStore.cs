using System.Globalization;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using VakifAtlas.Catalog.Api.Infrastructure.Persistence;
using VakifAtlas.Catalog.Domain;
using VakifAtlas.Catalog.Domain.Complexes;
using VakifAtlas.Catalog.Domain.Map;
using VakifAtlas.Catalog.Domain.References;
using VakifAtlas.Catalog.Domain.Works;
using VakifAtlas.ServiceDefaults.Http;
using VakifAtlas.SharedKernel;

namespace VakifAtlas.Catalog.Api.Infrastructure.Reading;

public enum WorkOrder
{
    Relevance, // relevance desc, title in Turkish order, id; cursor rank|title|id
    Updated,   // last change first (staff); cursor updated_at|id
}

// public catalog reads. Search, facets and map clustering run as raw SQL (folding, relevance, grouping) and return
// ids; rows are then loaded with EF. Visible = active, not deleted, in an active city
public sealed class CatalogReadStore(CatalogDbContext db, MediaUrls urls)
{
    public const int EmbeddedWorks = 20;
    public const int MaxPlaces = 5;

    public static readonly Expression<Func<Work, bool>> VisibleWork = w => w.IsActive && w.DeletedAt == null && w.City.IsActive;

    public static readonly Expression<Func<Complex, bool>> VisibleComplex = c => c.IsActive && c.DeletedAt == null && c.City.IsActive;

    public async Task<Result<(IReadOnlyList<long> Ids, string? NextCursor)>> SearchIdsAsync(
        WorkQuery query, int limit, string? cursor, bool publicOnly, CancellationToken cancellationToken,
        WorkOrder order = WorkOrder.Relevance)
    {
        var filter = new SqlFilter(query, publicOnly);
        return order == WorkOrder.Updated
            ? await ByUpdateAsync(filter, limit, cursor, cancellationToken)
            : await ByRelevanceAsync(filter, limit, cursor, cancellationToken);
    }

    public async Task<int> CountAsync(WorkQuery query, bool publicOnly, CancellationToken cancellationToken)
    {
        var filter = new SqlFilter(query, publicOnly);
        var rows = await db.Database.QueryAsync($"SELECT count(*)::int {filter.From} WHERE {filter.Where}", filter.Parameters,
            r => r.GetInt32(0), cancellationToken);
        return rows[0];
    }

    public async Task<Facets> FacetsAsync(WorkQuery query, CancellationToken cancellationToken)
    {
        var categories = await GroupCountAsync(query.WithoutCategories(), "w.category", cancellationToken);
        var periods = await GroupCountAsync(query.WithoutPeriods(), "w.period", cancellationToken);
        var filter = new SqlFilter(query.WithoutCities());
        var cities = await db.Database.QueryAsync($"""
            SELECT c.code, c.name, count(*)::int {filter.From} WHERE {filter.Where}
            GROUP BY c.code, c.name ORDER BY c.name COLLATE {CatalogDbContext.TurkishCollation}
            """, filter.Parameters, r => new CityFacet(r.GetInt16(0), r.GetString(1), r.GetInt32(2)), cancellationToken);
        return new Facets(categories, periods, cities);
    }

    // cities and districts the search names (cities first, at most MaxPlaces): a city's name must contain every
    // term; a district must, together with its city, contain every term and contain at least one itself. Only
    // places with works on the map are suggested (the suggestion moves the map); counts are the matching works
    public async Task<IReadOnlyList<Place>> PlacesAsync(WorkQuery query, CancellationToken cancellationToken)
    {
        if (query.Terms.Count == 0)
        {
            return [];
        }

        var filter = new SqlFilter(query.WithoutText());
        var terms = filter.Parameter(query.Terms.Select(SqlFilter.EscapeLike).ToArray());
        var take = filter.Parameter(MaxPlaces);
        return await db.Database.QueryAsync($"""
            WITH g AS (
              SELECT c.code, c.name AS city, w.district, count(*)::int AS n,
                     min(w.latitude) AS south, min(w.longitude) AS west, max(w.latitude) AS north, max(w.longitude) AS east
              {filter.From} WHERE {filter.Where}
              GROUP BY c.code, c.name, w.district HAVING count(w.latitude) > 0
            ), t AS (SELECT catalog_fold(term) AS term FROM unnest({terms}::text[]) term)
            SELECT * FROM (
              SELECT 0 AS kind, code, city, NULL::text AS district, sum(n)::int AS n, min(south), min(west), max(north), max(east)
              FROM g
              WHERE NOT EXISTS (SELECT 1 FROM t WHERE catalog_fold(g.city) NOT LIKE '%' || t.term || '%')
              GROUP BY code, city
              UNION ALL
              SELECT 1, code, city, district, n, south, west, north, east
              FROM g
              WHERE g.district IS NOT NULL
                AND NOT EXISTS (SELECT 1 FROM t WHERE catalog_fold(g.city || ' ' || g.district) NOT LIKE '%' || t.term || '%')
                AND EXISTS (SELECT 1 FROM t WHERE catalog_fold(g.district) LIKE '%' || t.term || '%')
            ) p
            ORDER BY kind, city COLLATE {CatalogDbContext.TurkishCollation}, district COLLATE {CatalogDbContext.TurkishCollation}
            LIMIT {take}
            """, filter.Parameters, r => new Place(new CityRef(r.GetInt16(1), r.GetString(2)), r.IsDBNull(3) ? null : r.GetString(3),
            r.GetInt32(4), [r.GetDouble(5), r.GetDouble(6), r.GetDouble(7), r.GetDouble(8)]), cancellationToken);
    }

    // summaries in the order of ids
    public async Task<IReadOnlyList<WorkSummary>> SummariesAsync(IReadOnlyList<long> ids, string language,
        CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var rows = await db.Works.AsNoTracking().Where(w => ids.Contains(w.Id)).Select(w => new
        {
            w.Id,
            w.Key,
            w.Category,
            w.Period,
            w.CityCode,
            City = w.City.Name,
            w.District,
            w.Address,
            w.Latitude,
            w.Longitude,
            Tr = w.Translations.Where(t => t.Locale == "tr")
                .Select(t => new { t.Title, t.Nickname, t.ShortDescription }).FirstOrDefault(),
            Local = w.Translations.Where(t => t.Locale == language)
                .Select(t => new { t.Title, t.Nickname, t.ShortDescription, t.Slug }).FirstOrDefault(),
            Cover = w.Images.OrderBy(i => i.Position).Select(i => i.Media).FirstOrDefault(),
            ComplexKey = w.Complex != null && w.Complex.IsActive && w.Complex.DeletedAt == null ? w.Complex.Key : null,
            ComplexTr = w.Complex!.Translations.Where(t => t.Locale == "tr").Select(t => t.Name).FirstOrDefault(),
            ComplexLocal = w.Complex!.Translations.Where(t => t.Locale == language).Select(t => t.Name).FirstOrDefault(),
        }).ToDictionaryAsync(w => w.Id, cancellationToken);

        return ids.Where(rows.ContainsKey).Select(id => rows[id]).Select(w => new WorkSummary(
            w.Key, w.Local?.Slug ?? w.Key, Pick(w.Local?.Title, w.Tr?.Title) ?? w.Key, Pick(w.Local?.Nickname, w.Tr?.Nickname),
            w.Category, w.Period, new CityRef(w.CityCode, w.City), w.District, w.Address, LocationOf(w.Latitude, w.Longitude),
            Pick(w.Local?.ShortDescription, w.Tr?.ShortDescription), w.Cover is null ? null : urls.ThumbnailUrl(MediaRef.Of(w.Cover)),
            w.ComplexKey is null ? null : new ComplexRef(w.ComplexKey, Pick(w.ComplexLocal, w.ComplexTr) ?? w.ComplexKey)))
            .ToArray();
    }

    public static WorkLocation ToLocation(WorkSummary summary) =>
        new(summary.Key, summary.Slug, summary.Title, summary.Category, summary.City, summary.District, summary.Location,
            summary.Complex);

    // by the tr URL (key) or the en slug; the request language's form wins when a key equals another work's en slug
    public async Task<WorkDetail?> WorkDetailAsync(string slug, string language, CancellationToken cancellationToken)
    {
        var candidates = await db.Works.AsNoTracking().Where(VisibleWork)
            .Where(w => w.Key == slug || w.Translations.Any(t => t.Locale == "en" && t.Slug == slug))
            .IncludeContent()
            .ToListAsync(cancellationToken);
        var work = language == "en"
            ? candidates.FirstOrDefault(w => w.Key != slug) ?? candidates.FirstOrDefault()
            : candidates.FirstOrDefault(w => w.Key == slug) ?? candidates.FirstOrDefault();
        if (work is null)
        {
            return null;
        }

        var tr = work.Translations.SingleOrDefault(t => t.Locale == "tr");
        var local = work.Translations.SingleOrDefault(t => t.Locale == language);
        var enSlug = work.Translations.SingleOrDefault(t => t.Locale == "en")?.Slug ?? work.Key;
        var complex = work.Complex is { IsActive: true, DeletedAt: null } c
            ? new ComplexRef(c.Key, Pick(c.Translations.SingleOrDefault(t => t.Locale == language)?.Name,
                c.Translations.SingleOrDefault(t => t.Locale == "tr")?.Name) ?? c.Key)
            : null;

        return new WorkDetail(
            work.Key, language == "en" ? enSlug : work.Key, new WorkSlugs(work.Key, enSlug),
            Pick(local?.Title, tr?.Title) ?? work.Key, Pick(local?.Nickname, tr?.Nickname), work.Category, work.Period,
            new CityRef(work.CityCode, work.City.Name), work.District, work.Address, LocationOf(work.Latitude, work.Longitude),
            Pick(local?.ShortDescriptionTitle, tr?.ShortDescriptionTitle), Pick(local?.ShortDescription, tr?.ShortDescription),
            Pick(local?.History, tr?.History), Pick(local?.ConstructionDate, tr?.ConstructionDate), Pick(local?.Founder, tr?.Founder),
            Pick(local?.Architect, tr?.Architect), work.IsUnescoWorldHeritage,
            work.Images.OrderBy(i => i.Position)
                .Select(i => Image(i.Media, language, i.Translations.Select(t => (t.Locale, t.AltText, t.Title)))).ToArray(),
            work.Parts.OrderBy(p => p.Position).Select(p =>
            {
                var partTr = p.Translations.SingleOrDefault(t => t.Locale == "tr");
                var partLocal = p.Translations.SingleOrDefault(t => t.Locale == language);
                return new PartResponse(Pick(partLocal?.Title, partTr?.Title), Pick(partLocal?.Description, partTr?.Description),
                    p.Images.OrderBy(i => i.Position)
                        .Select(i => Image(i.Media, language, i.Translations.Select(t => (t.Locale, t.AltText, t.Title)))).ToArray());
            }).ToArray(),
            complex);
    }

    // complexes by name (Turkish order), cursor name|id
    public async Task<Result<ComplexPage>> ComplexesAsync(string language, int limit, string? cursor,
        CancellationToken cancellationToken)
    {
        var complexes = db.Complexes.AsNoTracking().Where(VisibleComplex).Select(c => new
        {
            c.Id,
            c.Key,
            c.CityCode,
            City = c.City.Name,
            c.District,
            c.Address,
            c.Latitude,
            c.Longitude,
            Name = c.Translations.Where(t => t.Locale == language).Select(t => t.Name).FirstOrDefault()
                   ?? c.Translations.Where(t => t.Locale == "tr").Select(t => t.Name).FirstOrDefault()
                   ?? c.Key,
            Cover = c.Images.OrderBy(i => i.Position).Select(i => i.Media).FirstOrDefault(),
            WorkCount = c.Works.Count(w => w.IsActive && w.DeletedAt == null),
        });
        var total = await complexes.CountAsync(cancellationToken);

        if (!string.IsNullOrEmpty(cursor))
        {
            if (CursorCodec.Decode(cursor, 2) is not [var name, var rawId] ||
                !long.TryParse(rawId, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                return ApiError.BadRequest(ErrorCodes.InvalidCursor);
            }

            complexes = complexes.Where(c => string.Compare(c.Name, name) > 0 || (c.Name == name && c.Id > id));
        }

        var rows = await complexes.OrderBy(c => c.Name).ThenBy(c => c.Id).Take(limit + 1).ToListAsync(cancellationToken);
        string? next = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(limit);
            next = CursorCodec.Encode(rows[^1].Name, rows[^1].Id.ToString(CultureInfo.InvariantCulture));
        }

        return new ComplexPage(rows.Select(c => new ComplexSummary(c.Key, c.Name, new CityRef(c.CityCode, c.City), c.District,
            c.Address, LocationOf(c.Latitude, c.Longitude), c.Cover is null ? null : urls.ThumbnailUrl(MediaRef.Of(c.Cover)),
            c.WorkCount)).ToArray(), total, next);
    }

    public async Task<ComplexDetail?> ComplexDetailAsync(string key, string language, CancellationToken cancellationToken)
    {
        var complex = await db.Complexes.AsNoTracking().Where(VisibleComplex)
            .Include(c => c.City)
            .Include(c => c.Translations)
            .Include(c => c.Images).ThenInclude(i => i.Media)
            .AsSplitQuery()
            .SingleOrDefaultAsync(c => c.Key == key, cancellationToken);
        if (complex is null)
        {
            return null;
        }

        var works = await WorksOfAsync(key, language, EmbeddedWorks, null, cancellationToken);
        var points = (await PointsAsync([complex.Id], cancellationToken)).GetValueOrDefault(complex.Id, []);
        var name = Pick(complex.Translations.SingleOrDefault(t => t.Locale == language)?.Name,
            complex.Translations.SingleOrDefault(t => t.Locale == "tr")?.Name) ?? complex.Key;
        return new ComplexDetail(complex.Key, name, new CityRef(complex.CityCode, complex.City.Name), complex.District,
            complex.Address, LocationOf(complex.Latitude, complex.Longitude),
            complex.Images.OrderBy(i => i.Position).Select(i => Image(i.Media, language, [])).ToArray(),
            works.Value.Total, Footprint(points), ComplexGeometry.Bounds(points), works.Value);
    }

    public Task<bool> ComplexExistsAsync(string key, CancellationToken cancellationToken) =>
        db.Complexes.Where(VisibleComplex).AnyAsync(c => c.Key == key, cancellationToken);

    // works of a complex in curated order, cursor position|id
    public async Task<Result<WorkPage>> WorksOfAsync(string complexKey, string language, int limit, string? cursor,
        CancellationToken cancellationToken, IReadOnlyList<string>? categories = null)
    {
        var works = db.Works.AsNoTracking().Where(VisibleWork).Where(w => w.Complex!.Key == complexKey);
        if (categories is { Count: > 0 })
        {
            works = works.Where(w => categories.Contains(w.Category));
        }

        var total = await works.CountAsync(cancellationToken);
        if (!string.IsNullOrEmpty(cursor))
        {
            if (CursorCodec.Decode(cursor, 2) is not [var rawPosition, var rawId] ||
                !int.TryParse(rawPosition, NumberStyles.None, CultureInfo.InvariantCulture, out var position) ||
                !long.TryParse(rawId, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                return ApiError.BadRequest(ErrorCodes.InvalidCursor);
            }

            works = works.Where(w => w.ComplexPosition > position || (w.ComplexPosition == position && w.Id > id));
        }

        var page = await works.OrderBy(w => w.ComplexPosition).ThenBy(w => w.Id)
            .Select(w => new { w.Id, Position = w.ComplexPosition!.Value })
            .Take(limit + 1).ToListAsync(cancellationToken);
        string? next = null;
        if (page.Count > limit)
        {
            page.RemoveAt(limit);
            next = CursorCodec.Encode(page[^1].Position.ToString(CultureInfo.InvariantCulture),
                page[^1].Id.ToString(CultureInfo.InvariantCulture));
        }

        var items = await SummariesAsync(page.Select(p => p.Id).ToArray(), language, cancellationToken);
        return new WorkPage(items.ToArray<object>(), total, next);
    }

    // server-side clustering (archive ADR 29), response size independent of the catalog size. z < 15: markers
    // grouped in one GROUP BY over a 4×4 cell grid, a cell of one becomes the marker itself; 15 ≤ z < 17: every
    // marker; z ≥ 17: complexes open (footprint) and every matching work is a pin. Below 17 a work in a complex
    // with a location is represented by the complex (counted once)
    public async Task<IReadOnlyList<object>> TileAsync(WorkQuery query, int z, int x, int y, CancellationToken cancellationToken)
    {
        var (minX, maxX, minY, maxY) = MapTileMath.TileBounds(z, x, y);
        var filter = new SqlFilter(query);
        var bounds = new[] { filter.Parameter(minX), filter.Parameter(maxX), filter.Parameter(minY), filter.Parameter(maxY) };
        string Within(string alias) =>
            $"{alias}.mercator_x >= {bounds[0]} AND {alias}.mercator_x < {bounds[1]} AND " +
            $"{alias}.mercator_y >= {bounds[2]} AND {alias}.mercator_y < {bounds[3]}";

        if (z >= MapTileMath.ComplexExpandZoom)
        {
            var complexIds = await db.Database.QueryAsync($"{Representatives(filter, Within)} SELECT r.id FROM reps r WHERE r.kind = 'c'",
                filter.Parameters, r => r.GetInt64(0), cancellationToken);
            var workIds = await db.Database.QueryAsync($"SELECT w.id {filter.From} WHERE {filter.Where} AND {Within("w")}",
                filter.Parameters, r => r.GetInt64(0), cancellationToken);
            return [.. await TileComplexesAsync(complexIds, query.Language, footprint: true, cancellationToken),
                .. await TileWorksAsync(workIds, query.Language, cancellationToken)];
        }

        if (z >= MapTileMath.SinglesZoom)
        {
            var singles = await db.Database.QueryAsync($"{Representatives(filter, Within)} SELECT r.kind, r.id FROM reps r",
                filter.Parameters, r => (Kind: r.GetString(0), Id: r.GetInt64(1)), cancellationToken);
            return await MarkersAsync(singles, query.Language, cancellationToken);
        }

        var cells = filter.Parameter((double)MapTileMath.CellCount(z));
        var groups = await db.Database.QueryAsync($"""
            {Representatives(filter, Within)}
            SELECT floor(r.mercator_x * {cells})::int, floor(r.mercator_y * {cells})::int, count(*)::int,
                   avg(r.latitude), avg(r.longitude), min(r.latitude), min(r.longitude), max(r.latitude), max(r.longitude),
                   min(r.kind), min(r.id)
            FROM reps r
            GROUP BY 1, 2
            """, filter.Parameters, r => (CellX: r.GetInt32(0), CellY: r.GetInt32(1), Count: r.GetInt32(2),
            Lat: r.GetDouble(3), Lng: r.GetDouble(4), Bounds: new[] { r.GetDouble(5), r.GetDouble(6), r.GetDouble(7), r.GetDouble(8) },
            Kind: r.GetString(9), Id: r.GetInt64(10)), cancellationToken);

        var items = groups.Where(g => g.Count > 1)
            .Select(g => (object)new TileCluster($"{z}/{x}/{y}/{g.CellX}/{g.CellY}", g.Count, g.Lat, g.Lng, g.Bounds)).ToList();
        items.AddRange(await MarkersAsync(groups.Where(g => g.Count == 1).Select(g => (g.Kind, g.Id)).ToList(), query.Language,
            cancellationToken));
        return items;
    }

    public async Task<Extent> ExtentAsync(WorkQuery query, CancellationToken cancellationToken)
    {
        var filter = new SqlFilter(query);
        var rows = await db.Database.QueryAsync($"""
            {Representatives(filter, null)}
            SELECT (SELECT count(*) FROM matched m
                    WHERE m.mercator_x IS NOT NULL OR EXISTS (SELECT 1 FROM located k WHERE k.id = m.complex_id))::int,
                   min(r.latitude), min(r.longitude), max(r.latitude), max(r.longitude),
                   (SELECT version FROM catalog_meta WHERE id = {CatalogMeta.SingletonId})
            FROM reps r
            """, filter.Parameters, r => new Extent(r.GetInt32(0),
            r.IsDBNull(1) ? null : [r.GetDouble(1), r.GetDouble(2), r.GetDouble(3), r.GetDouble(4)],
            r.IsDBNull(5) ? "0" : r.GetInt64(5).ToString(CultureInfo.InvariantCulture)), cancellationToken);
        return rows[0];
    }

    // map markers (kind c = complex, w = work) of the matching works: a visible complex with a location stands for
    // its works, other works with a location stand for themselves. within: tile bounds per alias, null = all
    private static string Representatives(SqlFilter filter, Func<string, string>? within) => $"""
        WITH matched AS (
          SELECT w.id, w.complex_id, w.latitude, w.longitude, w.mercator_x, w.mercator_y
          {filter.From}
          WHERE {filter.Where}
        ), located AS (
          SELECT k.id, k.latitude, k.longitude, k.mercator_x, k.mercator_y FROM complexes k
          WHERE k.mercator_x IS NOT NULL AND k.is_active AND k.deleted_at IS NULL
        ), reps AS (
          SELECT 'c'::text AS kind, k.id, k.latitude, k.longitude, k.mercator_x, k.mercator_y
          FROM located k
          WHERE {within?.Invoke("k") ?? "TRUE"} AND EXISTS (SELECT 1 FROM matched m WHERE m.complex_id = k.id)
          UNION ALL
          SELECT 'w', m.id, m.latitude, m.longitude, m.mercator_x, m.mercator_y
          FROM matched m
          WHERE m.mercator_x IS NOT NULL AND {within?.Invoke("m") ?? "TRUE"}
            AND NOT EXISTS (SELECT 1 FROM located k WHERE k.id = m.complex_id)
        )
        """;

    private async Task<IReadOnlyList<object>> MarkersAsync(IReadOnlyList<(string Kind, long Id)> markers, string language,
        CancellationToken cancellationToken) =>
    [
        .. await TileComplexesAsync(markers.Where(m => m.Kind == "c").Select(m => m.Id).ToArray(), language, footprint: false,
            cancellationToken),
        .. await TileWorksAsync(markers.Where(m => m.Kind == "w").Select(m => m.Id).ToArray(), language, cancellationToken),
    ];

    private async Task<IReadOnlyList<TileWork>> TileWorksAsync(IReadOnlyList<long> ids, string language,
        CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var rows = await db.Works.AsNoTracking().Where(w => ids.Contains(w.Id) && w.Latitude != null).OrderBy(w => w.Key)
            .Select(w => new
            {
                w.Key,
                w.Category,
                w.CityCode,
                City = w.City.Name,
                w.District,
                Lat = w.Latitude!.Value,
                Lng = w.Longitude!.Value,
                Tr = w.Translations.Where(t => t.Locale == "tr").Select(t => t.Title).FirstOrDefault(),
                Local = w.Translations.Where(t => t.Locale == language).Select(t => new { t.Title, t.Slug }).FirstOrDefault(),
                ComplexKey = w.Complex != null && w.Complex.IsActive && w.Complex.DeletedAt == null ? w.Complex.Key : null,
                ComplexTr = w.Complex!.Translations.Where(t => t.Locale == "tr").Select(t => t.Name).FirstOrDefault(),
                ComplexLocal = w.Complex!.Translations.Where(t => t.Locale == language).Select(t => t.Name).FirstOrDefault(),
            }).ToListAsync(cancellationToken);
        return rows.Select(w => new TileWork(w.Key, w.Local?.Slug ?? w.Key, Pick(w.Local?.Title, w.Tr) ?? w.Key, w.Category,
            new CityRef(w.CityCode, w.City), w.District, w.Lat, w.Lng,
            w.ComplexKey is null ? null : new ComplexRef(w.ComplexKey, Pick(w.ComplexLocal, w.ComplexTr) ?? w.ComplexKey))).ToArray();
    }

    private async Task<IReadOnlyList<TileComplex>> TileComplexesAsync(IReadOnlyList<long> ids, string language, bool footprint,
        CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var rows = await db.Complexes.AsNoTracking().Where(c => ids.Contains(c.Id) && c.Latitude != null).OrderBy(c => c.Key)
            .Select(c => new
            {
                c.Id,
                c.Key,
                c.CityCode,
                City = c.City.Name,
                c.District,
                Lat = c.Latitude!.Value,
                Lng = c.Longitude!.Value,
                Name = c.Translations.Where(t => t.Locale == language).Select(t => t.Name).FirstOrDefault()
                       ?? c.Translations.Where(t => t.Locale == "tr").Select(t => t.Name).FirstOrDefault()
                       ?? c.Key,
                WorkCount = c.Works.Count(w => w.IsActive && w.DeletedAt == null),
            }).ToListAsync(cancellationToken);
        var points = footprint ? await PointsAsync(ids, cancellationToken) : new Dictionary<long, List<GeoPoint>>();
        return rows.Select(c => new TileComplex(c.Key, c.Name, new CityRef(c.CityCode, c.City), c.District, c.Lat, c.Lng,
            c.WorkCount, footprint ? Footprint(points.GetValueOrDefault(c.Id, [])) : null)).ToArray();
    }

    // located points of complexes: their own location and their visible works'
    private async Task<Dictionary<long, List<GeoPoint>>> PointsAsync(IReadOnlyList<long> complexIds, CancellationToken cancellationToken)
    {
        var own = await db.Complexes.AsNoTracking().Where(c => complexIds.Contains(c.Id) && c.Latitude != null)
            .Select(c => new { c.Id, Lat = c.Latitude!.Value, Lng = c.Longitude!.Value }).ToListAsync(cancellationToken);
        var works = await db.Works.AsNoTracking().Where(VisibleWork)
            .Where(w => w.ComplexId != null && complexIds.Contains(w.ComplexId.Value) && w.Latitude != null)
            .Select(w => new { Id = w.ComplexId!.Value, Lat = w.Latitude!.Value, Lng = w.Longitude!.Value })
            .ToListAsync(cancellationToken);
        return own.Concat(works).GroupBy(p => p.Id)
            .ToDictionary(g => g.Key, g => g.Select(p => new GeoPoint(p.Lat, p.Lng)).ToList());
    }

    private static double[][] Footprint(IEnumerable<GeoPoint> points) =>
        ComplexGeometry.Footprint(points).Select(p => new[] { p.Latitude, p.Longitude }).ToArray();

    private async Task<Result<(IReadOnlyList<long> Ids, string? NextCursor)>> ByRelevanceAsync(SqlFilter filter, int limit,
        string? cursor, CancellationToken cancellationToken)
    {
        var keyset = string.Empty;
        if (!string.IsNullOrEmpty(cursor))
        {
            var parts = CursorCodec.Decode(cursor, 3);
            if (parts is null || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rank) ||
                !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                return ApiError.BadRequest(ErrorCodes.InvalidCursor);
            }

            var r = filter.Parameter(rank);
            var n = filter.Parameter(parts[1]);
            var i = filter.Parameter(id);
            keyset = $"WHERE s.rank < {r} OR (s.rank = {r} AND (s.title > {n} OR (s.title = {n} AND s.id > {i})))";
        }

        var take = filter.Parameter(limit + 1);
        var rows = await db.Database.QueryAsync($"""
            SELECT s.id, s.title, s.rank FROM (
              SELECT w.id, {filter.Title} AS title, {filter.Rank} AS rank
              {filter.From}
              WHERE {filter.Where}
            ) s
            {keyset}
            ORDER BY s.rank DESC, s.title, s.id
            LIMIT {take}
            """, filter.Parameters, r => (Id: r.GetInt64(0), Title: r.IsDBNull(1) ? string.Empty : r.GetString(1), Rank: r.GetInt32(2)),
            cancellationToken);

        string? next = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(limit);
            var last = rows[^1];
            next = CursorCodec.Encode(last.Rank.ToString(CultureInfo.InvariantCulture), last.Title,
                last.Id.ToString(CultureInfo.InvariantCulture));
        }

        return (rows.Select(r => r.Id).ToArray(), next);
    }

    private async Task<Result<(IReadOnlyList<long> Ids, string? NextCursor)>> ByUpdateAsync(SqlFilter filter, int limit,
        string? cursor, CancellationToken cancellationToken)
    {
        var keyset = string.Empty;
        if (!string.IsNullOrEmpty(cursor))
        {
            if (CursorCodec.Decode(cursor, 2) is not [var rawUpdated, var rawId] || !CursorCodec.TryInstant(rawUpdated, out var updated) ||
                !long.TryParse(rawId, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                return ApiError.BadRequest(ErrorCodes.InvalidCursor);
            }

            keyset = $"AND (w.updated_at, w.id) < ({filter.Parameter(updated.ToUniversalTime())}, {filter.Parameter(id)})";
        }

        var take = filter.Parameter(limit + 1);
        var rows = await db.Database.QueryAsync($"""
            SELECT w.id, w.updated_at {filter.From}
            WHERE {filter.Where} {keyset}
            ORDER BY w.updated_at DESC, w.id DESC
            LIMIT {take}
            """, filter.Parameters, r => (Id: r.GetInt64(0), Updated: r.GetFieldValue<DateTimeOffset>(1)), cancellationToken);

        string? next = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(limit);
            next = CursorCodec.Encode(CursorCodec.Instant(rows[^1].Updated), rows[^1].Id.ToString(CultureInfo.InvariantCulture));
        }

        return (rows.Select(r => r.Id).ToArray(), next);
    }

    private async Task<IReadOnlyDictionary<string, int>> GroupCountAsync(WorkQuery query, string column,
        CancellationToken cancellationToken)
    {
        var filter = new SqlFilter(query);
        var rows = await db.Database.QueryAsync($"SELECT {column}, count(*)::int {filter.From} WHERE {filter.Where} GROUP BY {column}",
            filter.Parameters, r => (Key: r.GetString(0), Count: r.GetInt32(1)), cancellationToken);
        return rows.ToDictionary(r => r.Key, r => r.Count, StringComparer.Ordinal);
    }

    private ImageResponse Image(MediaAsset media, string language, IEnumerable<(string Locale, string? AltText, string? Title)> texts)
    {
        var all = texts.ToArray();
        var tr = all.FirstOrDefault(t => t.Locale == "tr");
        var local = all.FirstOrDefault(t => t.Locale == language);
        var reference = MediaRef.Of(media);
        var credit = MediaUrls.AttributionOf(reference);
        return new ImageResponse(urls.Url(reference), urls.ThumbnailUrl(reference), Pick(local.AltText, tr.AltText),
            credit.Author, credit.License, credit.SourceName, credit.SourceUrl, Pick(local.Title, tr.Title));
    }

    private static Location? LocationOf(double? latitude, double? longitude) =>
        latitude is { } lat && longitude is { } lng ? new Location(lat, lng) : null;

    private static string? Pick(string? local, string? tr) => string.IsNullOrWhiteSpace(local) ? tr : local;
}

internal static class WorkIncludes
{
    // everything a work page shows: texts, images, parts with their images, complex name
    public static IQueryable<Work> IncludeContent(this IQueryable<Work> works) => works
        .Include(w => w.City)
        .Include(w => w.Translations)
        .Include(w => w.Images).ThenInclude(i => i.Media)
        .Include(w => w.Images).ThenInclude(i => i.Translations)
        .Include(w => w.Parts).ThenInclude(p => p.Translations)
        .Include(w => w.Parts).ThenInclude(p => p.Images).ThenInclude(i => i.Media)
        .Include(w => w.Parts).ThenInclude(p => p.Images).ThenInclude(i => i.Translations)
        .Include(w => w.Complex).ThenInclude(c => c!.Translations)
        .AsSplitQuery();
}
