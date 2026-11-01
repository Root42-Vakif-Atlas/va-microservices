using System.Globalization;
using System.Text;
using Npgsql;

namespace VakifAtlas.Catalog.Api.Infrastructure.Reading;

// WorkQuery → parameterised SQL FROM / WHERE and a relevance expression. Aliases: w = works, c = cities,
// tr = tr translation, lt = request-language translation, x = complex. User input only travels as parameters;
// LIKE wildcards are escaped. Text is folded with catalog_fold (migration 0003) on both sides. The catalog is
// small (a few thousand works) and responses are cached, so the folded document is computed per query
// rather than stored; a document spanning four tables could not use one trigram index anyway
internal sealed class SqlFilter
{
    private readonly List<NpgsqlParameter> _parameters = [];
    private readonly List<string> _conditions = [];

    // publicOnly false (staff lists): every row, filtered by Publication and Located; founder, architect and
    // construction date are searched too
    public SqlFilter(WorkQuery query, bool publicOnly = true)
    {
        From = $"""
            FROM works w
            JOIN cities c ON c.code = w.city_code
            JOIN work_translations tr ON tr.work_id = w.id AND tr.locale = 'tr'
            LEFT JOIN work_translations lt ON lt.work_id = w.id AND lt.locale = {Parameter(query.Language)}
            LEFT JOIN complexes x ON x.id = w.complex_id
            """;

        if (publicOnly)
        {
            _conditions.Add("w.is_active AND w.deleted_at IS NULL AND c.is_active");
        }
        else
        {
            if (query.Publication == WorkQuery.Published)
            {
                _conditions.Add("w.deleted_at IS NULL");
            }
            else if (query.Publication == WorkQuery.Archived)
            {
                _conditions.Add("w.deleted_at IS NOT NULL");
            }

            if (query.Located == WorkQuery.Present)
            {
                _conditions.Add("w.latitude IS NOT NULL");
            }
            else if (query.Located == WorkQuery.Missing)
            {
                _conditions.Add("w.latitude IS NULL");
            }
        }

        if (query.Categories.Count > 0)
        {
            Add("w.category = ANY({0})", (object)query.Categories.ToArray());
        }

        if (query.Periods.Count > 0)
        {
            Add("w.period = ANY({0})", (object)query.Periods.ToArray());
        }

        if (query.Cities.Count > 0)
        {
            Add("w.city_code = ANY({0})", (object)query.Cities.ToArray());
        }

        if (query.Keys.Count > 0)
        {
            Add("w.key = ANY({0})", (object)query.Keys.ToArray());
        }

        if (query.Complex is { } complex)
        {
            Add("x.key = {0}", complex);
            if (publicOnly)
            {
                _conditions.Add("x.is_active AND x.deleted_at IS NULL");
            }
        }

        if (query.Bbox is var (south, west, north, east))
        {
            Add("w.latitude BETWEEN {0} AND {1} AND w.longitude BETWEEN {2} AND {3}", south, north, west, east);
        }

        var title = $"catalog_fold({Title})";
        var document = publicOnly
            ? "catalog_fold(concat_ws(' ', tr.title, lt.title, tr.nickname, lt.nickname, w.key, w.district, w.address, c.name))"
            : "catalog_fold(concat_ws(' ', tr.title, lt.title, tr.nickname, lt.nickname, w.key, w.district, w.address, c.name, " +
              "tr.founder, tr.architect, tr.construction_date))";
        var rank = new List<string>();
        foreach (var term in query.Terms)
        {
            var folded = $"catalog_fold({Parameter(EscapeLike(term))})";
            _conditions.Add($"{document} LIKE '%' || {folded} || '%'");
            // the weakest term decides: title start › title word start › in title › city / district › elsewhere
            rank.Add($"""
                CASE WHEN {title} LIKE {folded} || '%' THEN 4
                     WHEN ' ' || replace({title}, '-', ' ') LIKE '% ' || {folded} || '%' THEN 3
                     WHEN {title} LIKE '%' || {folded} || '%' THEN 2
                     WHEN catalog_fold(concat_ws(' ', c.name, w.district)) LIKE '%' || {folded} || '%' THEN 1
                     ELSE 0 END
                """);
        }

        Rank = rank.Count switch
        {
            0 => "0",
            1 => rank[0],
            _ => $"LEAST({string.Join(", ", rank)})",
        };
    }

    // title in the request language, tr when missing (column collation tr_icu)
    public string Title => "coalesce(lt.title, tr.title)";

    public string From { get; }

    public string Where => _conditions.Count == 0 ? "TRUE" : string.Join(" AND ", _conditions);

    public string Rank { get; }

    public IReadOnlyList<NpgsqlParameter> Parameters => _parameters;

    // extra condition (tile bounds, keyset cursor)
    public void Add(string template, params object[] values)
    {
        var names = values.Select(Parameter).ToArray<object>();
        _conditions.Add(string.Format(CultureInfo.InvariantCulture, template, names));
    }

    public string Parameter(object value)
    {
        var name = $"@p{_parameters.Count}";
        _parameters.Add(new NpgsqlParameter(name, value));
        return name;
    }

    public static string EscapeLike(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (character is '\\' or '%' or '_')
            {
                builder.Append('\\');
            }

            builder.Append(character);
        }

        return builder.ToString();
    }
}
