using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace VakifAtlas.ServiceDefaults.Http;

/// <summary>Content language from <c>Accept-Language</c>; defaults to Turkish.</summary>
public static class RequestLanguage
{
    /// <summary>Turkish, the default.</summary>
    public const string Turkish = "tr";

    /// <summary>English.</summary>
    public const string English = "en";

    /// <summary>Picks the best supported language from <c>Accept-Language</c>.</summary>
    /// <param name="request">The current request.</param>
    public static string Resolve(HttpRequest request)
    {
        if (StringWithQualityHeaderValue.TryParseList(request.Headers.AcceptLanguage, out IList<StringWithQualityHeaderValue>? values))
        {
            foreach (StringWithQualityHeaderValue value in values.OrderByDescending(v => v.Quality ?? 1))
            {
                var language = value.Value.Value?.Split('-', '_')[0].ToLowerInvariant();
                if (language is Turkish or English)
                {
                    return language;
                }
            }
        }

        return Turkish;
    }

    /// <summary>Maps a language code to a supported language.</summary>
    /// <param name="code">Language code to map.</param>
    public static string Normalize(string? code) =>
        string.Equals(code, English, StringComparison.OrdinalIgnoreCase) ? English : Turkish;
}
