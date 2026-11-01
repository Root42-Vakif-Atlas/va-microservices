using System.Text.RegularExpressions;

namespace VakifAtlas.Routes.Domain;

/// <summary>
/// Kaydedilen eser (ADR 40'ın sunucu karşılığı). Sahibi oturum açıksa hesap (<c>u:&lt;userId&gt;</c>),
/// değilse kurulum (<c>i:&lt;installationId&gt;</c>). Eser bilgisi tutulmaz; istemci katalogdan
/// güncel dilde çözer.
/// </summary>
public sealed class SavedMonument
{
    public const int MaxOwnerKeyLength = 64;

    public required string OwnerKey { get; init; }

    /// <summary>Kaydın yazıldığı kurulum (misafir sahipliğinde sahip anahtarıyla aynıdır).</summary>
    public Guid InstallationId { get; init; }

    public required string MonumentId { get; init; }

    public DateTimeOffset SavedAt { get; init; }
}

public sealed record SavedMonumentEntry(string MonumentId, DateTimeOffset SavedAt);

public static partial class SavedMonumentRules
{
    /// <summary>İstemcideki <c>SavedMonumentRules.maxSaved</c> ile aynı.</summary>
    public const int MaxSaved = 100;

    public const int MaxIdLength = 64;

    /// <summary>İstemci saatinin sapmasına tolerans; daha ileri tarihli kayıt reddedilir.</summary>
    public static readonly TimeSpan FutureTolerance = TimeSpan.FromDays(1);

    /// <summary>Kümenin tamamı geçerliyse <c>true</c>: ≤ 100 kayıt, kimlikler benzersiz ve biçimli.</summary>
    public static bool IsValid(IReadOnlyList<SavedMonumentEntry> items, DateTimeOffset now) =>
        items.Count <= MaxSaved &&
        items.Select(i => i.MonumentId).Distinct(StringComparer.Ordinal).Count() == items.Count &&
        items.All(i => MonumentId().IsMatch(i.MonumentId) && i.SavedAt <= now + FutureTolerance);

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex MonumentId();
}
