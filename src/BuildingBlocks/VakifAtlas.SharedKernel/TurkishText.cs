using System.Text;

namespace VakifAtlas.SharedKernel;

/// <summary>
/// Provides Turkish text folding, normalization, and collation utilities.
/// </summary>
public static class TurkishText
{
    private const string Alphabet = "abcçdefgğhıijklmnoöprsştuüvwxyz";

    private static readonly Dictionary<char, char> _folded = new()
    {
        ['ç'] = 'c',
        ['ğ'] = 'g',
        ['ı'] = 'i',
        ['ö'] = 'o',
        ['ş'] = 's',
        ['ü'] = 'u',
        ['â'] = 'a',
        ['î'] = 'i',
        ['û'] = 'u',
        ['é'] = 'e',
        ['è'] = 'e',
        ['ä'] = 'a',
    };

    /// <summary>
    /// Folds Turkish diacritics and accented characters into ASCII equivalents after lowercasing.
    /// Ensures variations like "SÜLEYMANİYE", "Süleymaniye", and "suleymaniye" produce identical output.
    /// </summary>
    /// <param name="value">The raw string to fold.</param>
    /// <returns>A normalized, accent-folded lowercase string.</returns>
    public static string Fold(string value)
    {
        var lower = ToLower(value);
        var builder = new StringBuilder(lower.Length);
        foreach (var character in lower)
        {
            builder.Append(_folded.TryGetValue(character, out var folded) ? folded : character);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Converts a string to lowercase applying Turkish casing rules (<c>I → ı</c>, <c>İ → i</c>).
    /// </summary>
    /// <param name="value">The string to lowercase.</param>
    /// <returns>The lowercased string under Turkish casing rules.</returns>
    public static string ToLower(string value) =>
        value.Replace('I', 'ı').Replace('İ', 'i').ToLowerInvariant();

    /// <summary>
    /// Tokenizes and folds a search query into distinct, non-empty whitespace-trimmed terms.
    /// </summary>
    /// <param name="query">The raw search query string.</param>
    /// <returns>A read-only list of normalized search tokens, or an empty list if null/whitespace.</returns>
    public static IReadOnlyList<string> Terms(string? query) =>
        string.IsNullOrWhiteSpace(query)
            ? []
            : Fold(query).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Compares two strings based on the standard Turkish alphabet ordering
    /// (handling specific character positions for Ç, Ğ, I/İ, Ö, Ş, Ü).
    /// </summary>
    /// <param name="left">The first string to compare.</param>
    /// <param name="right">The second string to compare.</param>
    /// <returns>
    /// A signed integer indicating the relative order: less than zero if <paramref name="left"/> precedes <paramref name="right"/>,
    /// zero if equal, or greater than zero if <paramref name="left"/> follows <paramref name="right"/>.
    /// </returns>
    public static int Compare(string? left, string? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return -1;
        }

        if (right is null)
        {
            return 1;
        }

        var a = ToLower(left);
        var b = ToLower(right);
        var length = Math.Min(a.Length, b.Length);
        for (var index = 0; index < length; index++)
        {
            var difference = Key(a[index]) - Key(b[index]);
            if (difference != 0)
            {
                return difference;
            }
        }

        return a.Length - b.Length;
    }

    /// <summary>
    /// A reusable <see cref="IComparer{T}"/> instance utilizing Turkish collation order.
    /// </summary>
    public static readonly IComparer<string> Comparer = Comparer<string>.Create(Compare);

    private static int Key(char character)
    {
        var position = Alphabet.IndexOf(character, StringComparison.Ordinal);
        return position >= 0 ? position : Alphabet.Length + character;
    }
}
