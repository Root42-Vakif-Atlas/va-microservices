using System.Text.RegularExpressions;
using VakifAtlas.SharedKernel;

namespace VakifAtlas.Catalog.Domain;

// import keys and URL slugs: lower-case a-z, digits and single inner hyphens (yesil-camii)
public static partial class CatalogKey
{
    public const int MaxLength = 200;

    public static bool IsValid(string? value) => value is { Length: > 0 and <= MaxLength } && Pattern().IsMatch(value);

    // slug from free text (a title); empty when the text has no letters or digits
    public static string From(string text)
    {
        var slug = Separators().Replace(TurkishText.Fold(text), "-").Trim('-');
        return slug.Length <= MaxLength ? slug : slug[..MaxLength].TrimEnd('-');
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex Pattern();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex Separators();
}
