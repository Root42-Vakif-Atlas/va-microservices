using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using VakifAtlas.Catalog.Domain;
using VakifAtlas.Catalog.Domain.Complexes;
using VakifAtlas.Catalog.Domain.Map;
using VakifAtlas.Catalog.Domain.Works;
using VakifAtlas.SharedKernel;

namespace VakifAtlas.Catalog.Api.Infrastructure.Import;

// root42 bulk file (seed/root42-vakifatlas.schema.json, formatVersion 1); the schema's
// additionalProperties:false is enforced by rejecting unknown members
public sealed record VgmFile(
    [property: JsonPropertyName("$schema")] string? Schema,
    int FormatVersion,
    List<VgmComplex>? Complexes,
    List<VgmWork>? Works);

public sealed record VgmName(string? Tr, string? En);

public sealed record VgmLanguages<T>(T? Tr, T? En)
    where T : class;

public sealed record VgmImageText(string? AltText, string? Title);

public sealed record VgmImage(string? Src, VgmLanguages<VgmImageText>? Translations);

// WGS 84; both or neither. Optional: VGM data has none, staff enter them in the panel
public sealed record VgmLocation(double? Lat, double? Lng)
{
    public GeoPoint? Point => Lat is { } lat && Lng is { } lng ? new GeoPoint(lat, lng) : null;
}

// works: member keys in display order; null = no members (file) or membership untouched (staff panel)
public sealed record VgmComplex(
    string? Key, VgmName? Name, int CityCode, string? District, string? Address, bool? IsActive, List<VgmImage>? Images,
    List<string>? Works, VgmLocation? Location = null);

public sealed record VgmWorkText(
    string? Slug, string? Title, string? Nickname, string? ShortDescriptionTitle, string? ShortDescription);

public sealed record VgmPartText(string? Title, string? Description);

public sealed record VgmPart(VgmLanguages<VgmPartText>? Translations, List<VgmImage>? Images);

public sealed record VgmWork(
    string? Key, int CityCode, string? Address, bool? IsActive, VgmLanguages<VgmWorkText>? Translations, List<VgmImage>? Images,
    List<VgmPart>? Parts, VgmLocation? Location = null);

// path = place in the file (works[12].images[0].src), code = stable code
public sealed record VgmIssue(string Path, string Code, string? Detail = null);

public sealed record VgmValidation(IReadOnlyList<VgmIssue> Errors, IReadOnlyList<VgmIssue> Warnings);

// column limits of the catalog schema
public static class CatalogLimits
{
    public const int Text = 200;
    public const int ShortDescription = 1000;
    public const int Address = 500;
    public const int District = 100;
    public const int AltText = 150;
    public const int Src = 300;
    public const int LongText = 20_000;
    public const int Url = 2000;
}

internal static class VgmText
{
    public static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static bool HasContent(VgmWorkText text) =>
        Clean(text.Title) is not null || Clean(text.Nickname) is not null || Clean(text.ShortDescriptionTitle) is not null ||
        Clean(text.ShortDescription) is not null;

    public static bool HasContent(VgmPartText text) => Clean(text.Title) is not null || Clean(text.Description) is not null;

    public static bool HasContent(VgmImageText text) => Clean(text.AltText) is not null || Clean(text.Title) is not null;
}

// parsing and file-level validation: keys, slugs, membership, city match, lengths, locations, images.
// checks that need the database (slugs of other works, complex city changes) are in CatalogImporter
public static partial class VgmValidator
{
    public const int FormatVersion = 1;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowTrailingCommas = false,
    };

    public static (VgmFile? File, VgmIssue? Error) Parse(string json)
    {
        try
        {
            var file = JsonSerializer.Deserialize<VgmFile>(json, Json);
            return file is null ? (null, new VgmIssue("$", "invalid_json")) : (file, null);
        }
        catch (JsonException exception)
        {
            return (null, new VgmIssue(exception.Path ?? "$", "invalid_json", exception.Message.Split('.')[0]));
        }
    }

    // imageAvailable: whether an image src can be resolved (file on disk hashed, uploaded image, …)
    public static VgmValidation Validate(VgmFile file, IReadOnlySet<short> cities, Func<string, bool> imageAvailable)
    {
        var errors = new List<VgmIssue>();
        var warnings = new List<VgmIssue>();
        void Error(string path, string code, string? detail = null) => errors.Add(new VgmIssue(path, code, detail));

        if (file.FormatVersion != FormatVersion)
        {
            Error("formatVersion", "unsupported_format_version");
        }

        if (file.Complexes is null)
        {
            Error("complexes", "required");
        }

        if (file.Works is null)
        {
            Error("works", "required");
        }

        var complexes = file.Complexes ?? [];
        var works = file.Works ?? [];

        var workKeys = new Dictionary<string, VgmWork>(StringComparer.Ordinal);
        var enSlugs = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < works.Count; i++)
        {
            var work = works[i];
            var path = $"works[{i}]";
            if (CheckKey(work.Key, $"{path}.key", Error) && !workKeys.TryAdd(work.Key!, work))
            {
                Error($"{path}.key", "duplicate_key", work.Key);
            }

            CheckCity(work.CityCode, cities, $"{path}.cityCode", Error);
            CheckText(work.Address, CatalogLimits.Address, $"{path}.address", required: false, Error);
            CheckLocation(work.Location, $"{path}.location", Error);
            var tr = work.Translations?.Tr;
            if (string.IsNullOrWhiteSpace(tr?.Title))
            {
                Error($"{path}.translations.tr.title", "required");
            }

            foreach (var (language, text) in new[] { ("tr", tr), ("en", work.Translations?.En) })
            {
                if (text is null)
                {
                    continue;
                }

                var textPath = $"{path}.translations.{language}";
                CheckText(text.Title, CatalogLimits.Text, $"{textPath}.title", required: false, Error);
                CheckText(text.Nickname, CatalogLimits.Text, $"{textPath}.nickname", required: false, Error);
                CheckText(text.ShortDescriptionTitle, CatalogLimits.Text, $"{textPath}.shortDescriptionTitle", required: false, Error);
                CheckText(text.ShortDescription, CatalogLimits.ShortDescription, $"{textPath}.shortDescription", required: false, Error);
                if (VgmText.Clean(text.Slug) is not { } slug)
                {
                    continue;
                }

                if (!CatalogKey.IsValid(slug))
                {
                    Error($"{textPath}.slug", "invalid_slug", slug);
                }
                else if (language == "tr")
                {
                    // the Turkish URL is the key itself
                    if (slug != work.Key)
                    {
                        warnings.Add(new VgmIssue($"{textPath}.slug", "tr_slug_ignored", slug));
                    }
                }
                else if (!enSlugs.Add(slug))
                {
                    Error($"{textPath}.slug", "duplicate_slug", slug);
                }
            }

            CheckImages(work.Images, $"{path}.images", null, imageAvailable, Error);
            var parts = work.Parts ?? [];
            for (var p = 0; p < parts.Count; p++)
            {
                var partPath = $"{path}.parts[{p}]";
                foreach (var (language, text) in new[] { ("tr", parts[p].Translations?.Tr), ("en", parts[p].Translations?.En) })
                {
                    CheckText(text?.Title, CatalogLimits.Text, $"{partPath}.translations.{language}.title", required: false, Error);
                }

                CheckImages(parts[p].Images, $"{partPath}.images", null, imageAvailable, Error);
            }

            if ((work.Images?.Count ?? 0) == 0)
            {
                warnings.Add(new VgmIssue($"{path}.images", "no_images"));
            }

            if (tr?.Title is { } title && WorkCategory.Of(title) == WorkCategory.Other)
            {
                warnings.Add(new VgmIssue($"{path}.translations.tr.title", "category_other", title));
            }
        }

        var titles = works.Select((w, i) => (Index: i, Title: w.Translations?.Tr?.Title))
            .Where(w => !string.IsNullOrWhiteSpace(w.Title))
            .GroupBy(w => TurkishText.Fold(w.Title!.Trim()))
            .Where(g => g.Count() > 1).SelectMany(g => g);
        warnings.AddRange(titles.Select(w => new VgmIssue($"works[{w.Index}].translations.tr.title", "duplicate_title", w.Title)));

        var complexKeys = new HashSet<string>(StringComparer.Ordinal);
        var memberOf = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < complexes.Count; i++)
        {
            var complex = complexes[i];
            var path = $"complexes[{i}]";
            if (CheckKey(complex.Key, $"{path}.key", Error) && !complexKeys.Add(complex.Key!))
            {
                Error($"{path}.key", "duplicate_key", complex.Key);
            }

            CheckCity(complex.CityCode, cities, $"{path}.cityCode", Error);
            CheckText(complex.Name?.Tr, CatalogLimits.Text, $"{path}.name.tr", required: true, Error);
            CheckText(complex.Name?.En, CatalogLimits.Text, $"{path}.name.en", required: false, Error);
            CheckText(complex.District, CatalogLimits.District, $"{path}.district", required: true, Error);
            CheckText(complex.Address, CatalogLimits.Address, $"{path}.address", required: false, Error);
            CheckLocation(complex.Location, $"{path}.location", Error);
            CheckImages(complex.Images, $"{path}.images", ComplexImage.MaxCount, imageAvailable, Error);
            if ((complex.Images?.Count ?? 0) == 0)
            {
                warnings.Add(new VgmIssue($"{path}.images", "no_images"));
            }

            var members = complex.Works ?? [];
            if (members.Distinct(StringComparer.Ordinal).Count() != members.Count)
            {
                Error($"{path}.works", "duplicate_member");
            }

            for (var m = 0; m < members.Count; m++)
            {
                var memberPath = $"{path}.works[{m}]";
                if (!workKeys.TryGetValue(members[m], out var member))
                {
                    Error(memberPath, "unknown_member", members[m]);
                    continue;
                }

                if (memberOf.TryGetValue(members[m], out var other) && other != complex.Key)
                {
                    Error(memberPath, "member_in_two_complexes", members[m]);
                }

                memberOf[members[m]] = complex.Key ?? string.Empty;
                if (member.CityCode != complex.CityCode)
                {
                    Error(memberPath, "member_city_mismatch", members[m]);
                }
            }
        }

        return new VgmValidation(errors, warnings);
    }

    // every image src in the file, in file order, distinct
    public static IReadOnlyList<string> ImageSources(VgmFile file) =>
        (file.Complexes ?? []).SelectMany(c => c.Images ?? [])
        .Concat((file.Works ?? []).SelectMany(w => (w.Images ?? []).Concat((w.Parts ?? []).SelectMany(p => p.Images ?? []))))
        .Select(i => i.Src).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();

    private static bool CheckKey(string? key, string path, Action<string, string, string?> error)
    {
        if (!CatalogKey.IsValid(key))
        {
            error(path, "invalid_key", key);
            return false;
        }

        return true;
    }

    private static void CheckCity(int code, IReadOnlySet<short> cities, string path, Action<string, string, string?> error)
    {
        if (code is < short.MinValue or > short.MaxValue || !cities.Contains((short)code))
        {
            error(path, "unknown_city", code.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void CheckLocation(VgmLocation? location, string path, Action<string, string, string?> error)
    {
        if (location is null)
        {
            return;
        }

        if (location.Point is not { } point)
        {
            error(path, "incomplete_location", null);
        }
        else if (!point.IsValid || !CatalogRegion.Contains(point.Latitude, point.Longitude))
        {
            error(path, "location_out_of_range", null);
        }
    }

    private static void CheckText(string? value, int max, string path, bool required, Action<string, string, string?> error)
    {
        if (required && string.IsNullOrWhiteSpace(value))
        {
            error(path, "required", null);
        }
        else if (value is not null && value.Trim().Length > max)
        {
            error(path, "too_long", null);
        }
    }

    private static void CheckImages(List<VgmImage>? images, string path, int? max, Func<string, bool> imageAvailable,
        Action<string, string, string?> error)
    {
        if (images is null)
        {
            return;
        }

        if (images.Count > max)
        {
            error(path, "too_many_images", null);
        }

        for (var i = 0; i < images.Count; i++)
        {
            var image = images[i];
            var imagePath = $"{path}[{i}]";
            if (image.Src is null || image.Src.Length > CatalogLimits.Src || !SrcPattern().IsMatch(image.Src))
            {
                error($"{imagePath}.src", "invalid_src", image.Src);
            }
            else if (!imageAvailable(image.Src))
            {
                error($"{imagePath}.src", "missing_image", image.Src);
            }

            foreach (var (language, text) in new[] { ("tr", image.Translations?.Tr), ("en", image.Translations?.En) })
            {
                CheckText(text?.AltText, CatalogLimits.AltText, $"{imagePath}.translations.{language}.altText", required: false, error);
                CheckText(text?.Title, CatalogLimits.Text, $"{imagePath}.translations.{language}.title", required: false, error);
            }
        }
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*(/[a-z0-9]+(-[a-z0-9]+)*)*\\.(jpg|jpeg|png|webp)$")]
    private static partial Regex SrcPattern();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    public static partial Regex Sha256Pattern();
}
