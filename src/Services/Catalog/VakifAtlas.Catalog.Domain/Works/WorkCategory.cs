using System.Text.RegularExpressions;
using VakifAtlas.SharedKernel;

namespace VakifAtlas.Catalog.Domain.Works;

// building type, derived from the Turkish title (not stored). The head noun of a Turkish compound comes last:
// "Muradiye Camii Şadırvanı" is a fountain, not a mosque
public static partial class WorkCategory
{
    public const string Other = "other";

    public static readonly IReadOnlyList<string> All =
        ["mosque", "madrasa", "inn", "caravanserai", "bath", "fountain", "tomb", "hospital", "imaret", Other];

    // folded stems; short stems match whole words, long ones match as prefixes
    private static readonly (string Stem, string Category, bool Prefix)[] Keywords =
    [
        ("cami", "mosque", false), ("camii", "mosque", false), ("camisi", "mosque", false), ("mescid", "mosque", true),
        ("mescit", "mosque", true), ("medrese", "madrasa", true), ("darulhadis", "madrasa", true), ("turbe", "tomb", true),
        ("kumbet", "tomb", true), ("han", "inn", false), ("hani", "inn", false), ("bedesten", "inn", true),
        ("kervansaray", "caravanserai", true), ("hamam", "bath", true), ("kaplica", "bath", true),
        ("cesme", "fountain", true), ("sebil", "fountain", true), ("sadirvan", "fountain", true),
        ("imaret", "imaret", true), ("darussifa", "hospital", true), ("sifahane", "hospital", true),
        ("bimarhane", "hospital", true),
    ];

    public static string Of(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return Other;
        }

        var words = WordSplit().Split(TurkishText.Fold(title)).Where(w => w.Length > 0).ToArray();
        for (var i = words.Length - 1; i >= 0; i--)
        {
            foreach (var (stem, category, prefix) in Keywords)
            {
                if (prefix ? words[i].StartsWith(stem, StringComparison.Ordinal) : words[i] == stem)
                {
                    return category;
                }
            }
        }

        return Other;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex WordSplit();
}
