namespace VakifAtlas.ServiceDefaults.Http;

/// <summary>Parsers for common query parameters.</summary>
public static class QueryParsing
{
    /// <summary>Splits a comma-separated value into distinct items.</summary>
    /// <param name="value">Raw query value.</param>
    public static IReadOnlyList<string> CommaList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal).ToArray();

    /// <summary>Parses <c>limit</c>; returns <c>null</c> if invalid.</summary>
    /// <param name="value">Raw query value.</param>
    /// <param name="defaultValue">Value used when <paramref name="value"/> is empty.</param>
    /// <param name="max">Largest allowed value.</param>
    public static int? Limit(string? value, int defaultValue, int max)
    {
        if (string.IsNullOrEmpty(value))
        {
            return defaultValue;
        }

        return int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var limit)
            && limit >= 1 && limit <= max
            ? limit
            : null;
    }

    /// <summary>Parses an optional <c>yyyy-MM-dd</c> date.</summary>
    /// <param name="value">Raw query value.</param>
    /// <param name="date">The parsed date, or <c>null</c>.</param>
    public static bool TryDate(string? value, out DateOnly? date)
    {
        date = null;
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out DateOnly parsed))
        {
            date = parsed;
            return true;
        }

        return false;
    }
}
