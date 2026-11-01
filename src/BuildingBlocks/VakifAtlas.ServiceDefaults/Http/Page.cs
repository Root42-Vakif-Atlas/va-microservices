namespace VakifAtlas.ServiceDefaults.Http;

/// <summary>Cursor-paginated list; <c>NextCursor</c> is <c>null</c> on the last page.</summary>
/// <typeparam name="T">Item type.</typeparam>
/// <param name="Items">Items on this page.</param>
/// <param name="Total">Total matching items.</param>
/// <param name="NextCursor">Cursor for the next page.</param>
public record Page<T>(IReadOnlyList<T> Items, int Total, string? NextCursor);
