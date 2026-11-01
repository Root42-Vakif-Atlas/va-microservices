using System.Globalization;
using VakifAtlas.Catalog.Api.Infrastructure;
using VakifAtlas.Catalog.Api.Infrastructure.Import;
using VakifAtlas.Catalog.Api.Infrastructure.Reading;
using VakifAtlas.ServiceDefaults.Admin;

namespace VakifAtlas.Catalog.Api.Features.Admin;

// staff panel shapes follow the import file (VgmWork / VgmComplex) plus the fields only staff set

public sealed record AdminAttribution(string? Author, string? License, string? SourceName, string? SourceUrl);

// exactly one source: Sha256 (uploaded through PUT imports/images/{sha256}), UploadId (photo upload ticket,
// POST image-uploads) or Url (an image elsewhere; mirrored into storage, attribution required).
// Attribution, when given, is stored on the image file and applies wherever the file is used
public sealed record AdminImageInput(
    string? Sha256,
    string? UploadId,
    string? Url,
    AdminAttribution? Attribution,
    VgmLanguages<VgmImageText>? Translations);

public sealed record AdminPartInput(VgmLanguages<VgmPartText>? Translations, IReadOnlyList<AdminImageInput>? Images);

public sealed record AdminLocation(double? Latitude, double? Longitude);

public sealed record AdminWorkText(
    string? Slug,
    string? Title,
    string? Nickname,
    string? ShortDescriptionTitle,
    string? ShortDescription,
    string? History,
    string? ConstructionDate,
    string? Founder,
    string? Architect);

// key only on create; membership is set on the complex (PUT complexes/{key}/works). Category null = follow the
// Turkish title; period null = other; location null = not on the map
public sealed record AdminWorkInput(
    string? Key,
    int CityCode,
    string? District,
    string? Address,
    string? Category,
    string? Period,
    bool? IsUnescoWorldHeritage,
    AdminLocation? Location,
    bool? IsActive,
    VgmLanguages<AdminWorkText>? Translations,
    IReadOnlyList<AdminImageInput>? Images,
    IReadOnlyList<AdminPartInput>? Parts);

public sealed record AdminComplexInput(
    string? Key,
    VgmName? Name,
    int CityCode,
    string? District,
    string? Address,
    AdminLocation? Location,
    bool? IsActive,
    IReadOnlyList<AdminImageInput>? Images);

public sealed record ComplexWorksRequest(IReadOnlyList<string>? WorkKeys);

// Mirrored false: added by URL and not copied yet (MirrorError says why, after a failure)
public sealed record AdminImage(
    long MediaId,
    string? Sha256,
    string? OriginUrl,
    string Url,
    string ThumbnailUrl,
    bool Mirrored,
    string? MirrorError,
    Attribution Attribution,
    VgmLanguages<VgmImageText> Translations);

public sealed record AdminPart(VgmLanguages<VgmPartText> Translations, IReadOnlyList<AdminImage> Images);

public sealed record AdminComplexRef(string Key, string Name, int? Position);

public sealed record AdminWorkDetail(
    string Key,
    short CityCode,
    string? District,
    string? Address,
    string Category,
    bool CategoryOverridden,
    string Period,
    bool IsUnescoWorldHeritage,
    Location? Location,
    bool IsActive,
    VgmLanguages<AdminWorkText> Translations,
    IReadOnlyList<AdminImage> Images,
    IReadOnlyList<AdminPart> Parts,
    AdminComplexRef? Complex,
    string Publication,
    DateTimeOffset? DeletedAt,
    DateTimeOffset UpdatedAt);

public sealed record AdminWorkSummary(
    string Key,
    VgmName Title,
    string Category,
    string Period,
    CityRef City,
    string? District,
    string? ThumbnailUrl,
    int ImageCount,
    bool HasLocation,
    AdminComplexRef? Complex,
    bool IsActive,
    string Publication,
    DateTimeOffset UpdatedAt);

public sealed record AdminMember(string Key, string? Title, int Position, bool IsActive, bool HasLocation, string Publication);

public sealed record AdminComplexDetail(
    string Key,
    VgmName Name,
    short CityCode,
    string District,
    string? Address,
    Location? Location,
    bool IsActive,
    IReadOnlyList<AdminImage> Images,
    IReadOnlyList<AdminMember> Works,
    string Publication,
    DateTimeOffset? DeletedAt,
    DateTimeOffset UpdatedAt);

public sealed record AdminComplexSummary(
    string Key,
    VgmName Name,
    short CityCode,
    string District,
    int WorkCount,
    bool HasLocation,
    bool IsActive,
    string Publication,
    DateTimeOffset UpdatedAt);

// counts of the published works (not deleted)
public sealed record CatalogAdminSummary(
    int Works,
    int Published,
    int Archived,
    int Inactive,
    int WithoutImages,
    int WithoutLocation,
    int Complexes,
    int PendingImages,
    IReadOnlyDictionary<string, int> ByCategory,
    IReadOnlyDictionary<string, int> ByPeriod,
    IReadOnlyList<CityFacet> ByCity,
    string Version);

// 400 body of a rejected save: issues use the input's paths (images[0].sha256)
public sealed record AdminValidationError(string Code, IReadOnlyList<VgmIssue> Errors);

public static class AdminRules
{
    public const int MaxMembers = 500;
    public const int MaxImages = 120;
    public const int MaxUploadIdLength = 40;

    // revision is bumped by trigger on every update, also a soft delete
    public static string ETagOf(int revision) => Preconditions.ETagOf(revision.ToString(CultureInfo.InvariantCulture));

    public static string PublicationOf(DateTimeOffset? deletedAt) => deletedAt is null ? WorkQuery.Published : WorkQuery.Archived;
}
