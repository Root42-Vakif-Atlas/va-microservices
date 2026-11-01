namespace VakifAtlas.Identity.Domain;

/// <summary>Kullanım Koşulları / Aydınlatma Metni; sürüm, kayıttaki onayla birlikte saklanır.</summary>
public sealed class LegalDocument
{
    public const string TermsOfUse = "terms-of-use";
    public const string PrivacyNotice = "privacy-notice";

    public static readonly IReadOnlySet<string> Types = new HashSet<string>(StringComparer.Ordinal) { TermsOfUse, PrivacyNotice };

    public required string Type { get; init; }

    public required string Language { get; init; }

    public required string Version { get; set; }

    public required string Title { get; set; }

    public required string Body { get; set; }

    public DateOnly EffectiveDate { get; set; }
}
