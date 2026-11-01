using System.Security.Cryptography;

namespace VakifAtlas.SharedKernel;

/// <summary>
/// Provides factory methods for generating cryptographically secure,
/// prefixed unique identifiers (e.g., <c>rt_3f9c…</c>, <c>mem_…</c>).
/// </summary>
public static class Ids
{
    /// <summary>
    /// Generates a prefixed, URL-safe unique identifier using cryptographically secure random bytes.
    /// </summary>
    /// <param name="prefix">The entity or resource prefix (e.g., "rt", "mem").</param>
    /// <param name="bytes">The number of random bytes to generate before lowercase hex encoding. Defaults to 12.</param>
    /// <returns>A string identifier in the format <c>{prefix}_{hex}</c>.</returns>
    public static string New(string prefix, int bytes = 12) =>
        $"{prefix}_{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(bytes))}";
}
