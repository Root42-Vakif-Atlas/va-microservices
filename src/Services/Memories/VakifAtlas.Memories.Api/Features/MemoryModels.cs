using System.Globalization;
using System.Text.Json.Serialization;
using VakifAtlas.Media;
using VakifAtlas.Memories.Domain;
using VakifAtlas.ServiceDefaults.Http;
using VakifAtlas.SharedKernel;

namespace VakifAtlas.Memories.Api.Features;

public sealed record CreateMemoryRequest(string? MonumentId, string? Text, string? VisitedOn, List<PhotoAttachment>? Photos);

public sealed record ReportRequest(string? Reason, string? Note);

public sealed record AuthorResponse(string Id, string DisplayName);

public sealed record PhotoResponse(string Id, string Url, string ThumbnailUrl, int Width, int Height, string? Description);

public sealed record MemoryResponse(
    string Id,
    string MonumentId,
    AuthorResponse Author,
    string Text,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? VisitedOn,
    IReadOnlyList<PhotoResponse> Photos,
    DateTimeOffset CreatedAt,
    string MemoryDate,
    string Status)
{
    public static MemoryResponse From(Memory memory, StorageOptions storage) => new(
        memory.Id, memory.MonumentId, new AuthorResponse(memory.AuthorId, memory.AuthorName), memory.Text,
        memory.VisitedOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        memory.Photos.Select(p => new PhotoResponse(p.Id, storage.PublicUrl(p.Key), storage.PublicUrl(p.ThumbnailKey), p.Width,
            p.Height, p.Description)).ToArray(),
        memory.CreatedAt, memory.MemoryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), memory.Status);
}

public sealed record YearCount(int Year, int Count);

public sealed record MemoryFacets(IReadOnlyList<YearCount> Years);

public sealed record MemoryPage(
    IReadOnlyList<MemoryResponse> Items,
    int Total,
    string? NextCursor,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MemoryFacets? Facets) : Page<MemoryResponse>(Items, Total, NextCursor);

/// <summary>Keyset imleçleri: zaman çizelgesi <c>memoryDate|createdAt|id</c>, "Anılarım" <c>createdAt|id</c>.</summary>
internal static class MemoryCursors
{
    public static string Timeline(Memory memory) => CursorCodec.Encode(
        memory.MemoryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), CursorCodec.Instant(memory.CreatedAt), memory.Id);

    public static string Shared(Memory memory) => CursorCodec.EncodeInstantId(memory.CreatedAt, memory.Id);

    public static (DateOnly Date, DateTimeOffset CreatedAt, string Id)? ParseTimeline(string cursor) =>
        CursorCodec.Decode(cursor, 3) is [var date, var created, var id] &&
        DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) &&
        CursorCodec.TryInstant(created, out var instant)
            ? (day, instant, id)
            : null;

    public static (DateTimeOffset CreatedAt, string Id)? ParseShared(string cursor) => CursorCodec.DecodeInstantId(cursor);
}
