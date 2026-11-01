using System.Text.RegularExpressions;
using VakifAtlas.SharedKernel;

namespace VakifAtlas.Catalog.Api.Infrastructure.Import;

// the bulk file has no district for works; it is read from the address when it ends in "(District/City)",
// is just "District/City", or starts "City - District …", and the city matches the work's city
public static partial class AddressParser
{
    public static string? District(string? address, string city)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        var text = address.Trim();
        var cityFolded = TurkishText.Fold(city);
        foreach (var match in new[] { TrailingPlace().Match(text), WholePlace().Match(text) })
        {
            if (match.Success && TurkishText.Fold(match.Groups[2].Value.Trim()) == cityFolded)
            {
                return Clip(match.Groups[1].Value.Trim());
            }
        }

        var leading = LeadingCity().Match(text);
        return leading.Success && TurkishText.Fold(leading.Groups[1].Value) == cityFolded
            ? Clip(leading.Groups[2].Value.Trim())
            : null;
    }

    private static string? Clip(string district) => district.Length is > 0 and <= CatalogLimits.District ? district : null;

    [GeneratedRegex(@"\(([^()/]+)/([^()/]+)\)\s*$")]
    private static partial Regex TrailingPlace();

    [GeneratedRegex(@"^([^/\n]+)/([^/\n]+)$")]
    private static partial Regex WholePlace();

    [GeneratedRegex(@"^(\p{L}+)\s+-\s+(\p{L}+)")]
    private static partial Regex LeadingCity();
}
