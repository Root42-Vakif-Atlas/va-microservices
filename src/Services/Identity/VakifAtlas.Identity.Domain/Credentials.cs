using System.Globalization;
using System.Text.RegularExpressions;

namespace VakifAtlas.Identity.Domain;

/// <summary>Kimlik formlarının alan kuralları (<c>credentials_validator.dart</c> ile aynı).</summary>
public static partial class Credentials
{
    public const int MaxEmailLength = 254;
    public const int MinDisplayNameLength = 2;
    public const int MaxDisplayNameLength = 80;
    public const int CodeLength = 6;

    public static string NormalizeEmail(string value) => value.Trim().ToLower(CultureInfo.InvariantCulture);

    public static string NormalizeDisplayName(string value) => Whitespace().Replace(value.Trim(), " ");

    public static string NormalizeCode(string value) => Whitespace().Replace(value, string.Empty);

    public static bool IsValidEmail(string value)
    {
        var email = value.Trim();
        return email.Length is > 0 and <= MaxEmailLength && Email().IsMatch(email);
    }

    public static bool IsValidDisplayName(string value)
    {
        var name = NormalizeDisplayName(value);
        var length = name.EnumerateRunes().Count();
        return length is >= MinDisplayNameLength and <= MaxDisplayNameLength && DisplayName().IsMatch(name);
    }

    public static bool IsWellFormedCode(string value)
    {
        var code = NormalizeCode(value);
        return code.Length == CodeLength && code.All(char.IsAsciiDigit);
    }

    [GeneratedRegex(@"^[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}$")]
    private static partial Regex Email();

    [GeneratedRegex(@"^[\p{L}\p{M}]+(?:[ '’.-][\p{L}\p{M}]+)*\.?$")]
    private static partial Regex DisplayName();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
