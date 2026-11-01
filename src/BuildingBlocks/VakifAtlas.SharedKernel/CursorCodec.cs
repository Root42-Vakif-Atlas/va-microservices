using System.Buffers.Text;
using System.Text;

namespace VakifAtlas.SharedKernel;

/// <summary>
/// Provides utilities for encoding and decoding opaque keyset pagination cursors.
/// Values are joined with a delimiter and encoded using unpadded Base64Url.
/// The internal structure is treated as opaque by clients.
/// </summary>
public static class CursorCodec
{
    private const char Separator = '|';

    /// <summary>
    /// Encodes arbitrary string tokens into a Base64Url cursor string.
    /// </summary>
    /// <param name="parts">The individual values that compose the keyset cursor.</param>
    /// <returns>An unpadded Base64Url-encoded cursor.</returns>
    public static string Encode(params string[] parts) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes(string.Join(Separator, parts)));

    /// <summary>
    /// Decodes a cursor back into its constituent parts.
    /// </summary>
    /// <param name="cursor">The raw Base64Url cursor string to decode.</param>
    /// <param name="expectedParts">The exact number of delimited parts expected.</param>
    /// <returns>
    /// An array of parsed values if valid and matching <paramref name="expectedParts"/>;
    /// otherwise, <see langword="null"/>.
    /// </returns>
    public static string[]? Decode(string? cursor, int expectedParts)
    {
        if (string.IsNullOrEmpty(cursor) || cursor.Length > 1024)
        {
            return null;
        }

        try
        {
            var text = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor));
            var parts = text.Split(Separator, expectedParts);
            return parts.Length == expectedParts ? parts : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Encodes a timestamp and unique identifier into a standard keyset cursor (<c>timestamp|id</c>).
    /// </summary>
    /// <param name="instant">The timestamp boundary for pagination.</param>
    /// <param name="id">The unique identifier tie-breaker.</param>
    /// <returns>An unpadded Base64Url-encoded cursor.</returns>
    public static string EncodeInstantId(DateTimeOffset instant, string id) => Encode(Instant(instant), id);

    /// <summary>
    /// Decodes a cursor representing a timestamp and identifier pair (<c>timestamp|id</c>).
    /// </summary>
    /// <param name="cursor">The raw cursor string to decode.</param>
    /// <returns>
    /// A tuple containing the parsed <see cref="DateTimeOffset"/> and identifier if valid;
    /// otherwise, <see langword="null"/>.
    /// </returns>
    public static (DateTimeOffset Instant, string Id)? DecodeInstantId(string? cursor) =>
        Decode(cursor, 2) is [var created, var id] && TryInstant(created, out var instant) ? (instant, id) : null;

    /// <summary>
    /// Formats a timestamp into an invariant, round-trip ISO-8601 UTC string.
    /// </summary>
    /// <param name="value">The timestamp to format.</param>
    /// <returns>An ISO-8601 formatted UTC date string using the standard round-trip ("O") specifier.</returns>
    public static string Instant(DateTimeOffset value) =>
        value.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Attempts to parse an ISO-8601 round-trip formatted date string back into a <see cref="DateTimeOffset"/>.
    /// </summary>
    /// <param name="value">The formatted string representation of the date.</param>
    /// <param name="instant">When this method returns, contains the parsed timestamp if successful.</param>
    /// <returns><see langword="true"/> if parsing succeeded; otherwise, <see langword="false"/>.</returns>
    public static bool TryInstant(string value, out DateTimeOffset instant) =>
        DateTimeOffset.TryParseExact(value, "O", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out instant);
}
