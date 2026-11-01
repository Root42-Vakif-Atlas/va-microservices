using System.Text.RegularExpressions;

namespace VakifAtlas.Feedback.Domain;

/// <summary>
/// Uygulama deneyimi hakkında geri bildirim (Ayarlar → Hakkında → "Geri bildirim gönder"). Giriş
/// gerekmez; oturum açıksa kullanıcıya bağlanır (hesap silinince bağ kaldırılır, kayıt kalır). Metin
/// kişisel veri içerebileceğinden loglanmaz; yalnızca VGM personeli okur.
/// </summary>
public sealed class FeedbackEntry
{
    public required string Id { get; init; }

    public required string Category { get; init; }

    /// <summary>Genel deneyim puanı (1–5); isteğe bağlı.</summary>
    public int? Rating { get; init; }

    /// <summary>Boş olabilir (yalnızca puan); en çok <see cref="FeedbackRules.MaxMessage"/> karakter.</summary>
    public required string Message { get; init; }

    public string? AppVersion { get; init; }

    public string? Platform { get; init; }

    public required string Language { get; init; }

    /// <summary>Oturum açıksa kullanıcı; misafirde veya hesap silinince <c>null</c>.</summary>
    public string? UserId { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>VGM triajı (<see cref="FeedbackStatus"/>); yeni kayıt <c>new</c>.</summary>
    public string Status { get; private set; } = FeedbackStatus.New;

    /// <summary>Personelin iç notu (gönderene gösterilmez).</summary>
    public string? StaffNote { get; private set; }

    public string? HandledBy { get; private set; }

    public string? HandledByName { get; private set; }

    public DateTimeOffset? HandledAt { get; private set; }

    public void Triage(string status, string? note, string actorId, string actorName, DateTimeOffset now)
    {
        Status = status;
        StaffNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        HandledBy = actorId;
        HandledByName = actorName;
        HandledAt = now;
    }
}

public static class FeedbackStatus
{
    public const string New = "new";
    public const string InProgress = "inProgress";
    public const string Done = "done";
    public const string Dismissed = "dismissed";

    public static readonly IReadOnlyList<string> All = [New, InProgress, Done, Dismissed];
}

public static class FeedbackCategory
{
    public const string Bug = "bug";
    public const string Suggestion = "suggestion";
    public const string Accessibility = "accessibility";
    public const string Content = "content";
    public const string Other = "other";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { Bug, Suggestion, Accessibility, Content, Other };
}

public static partial class FeedbackRules
{
    public const int MaxMessage = 2000;
    public const int MinRating = 1;
    public const int MaxRating = 5;

    /// <summary>Kullanıcı (veya misafirde istemci adresi) başına saatlik sınır.</summary>
    public const int MaxPerHour = 5;

    public const int MaxStaffNote = 1000;

    public static readonly IReadOnlySet<string> Platforms =
        new HashSet<string>(StringComparer.Ordinal) { "android", "ios", "web", "macos", "windows", "linux" };

    /// <summary>Kategori zorunlu; puan veya metinden en az biri verilmelidir.</summary>
    public static bool IsValid(string? category, int? rating, string? message, string? appVersion, string? platform)
    {
        var text = message?.Trim() ?? string.Empty;
        return category is not null && FeedbackCategory.All.Contains(category) &&
               rating is null or (>= MinRating and <= MaxRating) &&
               text.Length <= MaxMessage &&
               (rating is not null || text.Length > 0) &&
               (appVersion is null || AppVersion().IsMatch(appVersion)) &&
               (platform is null || Platforms.Contains(platform));
    }

    [GeneratedRegex(@"^[0-9A-Za-z.+\-]{1,40}$")]
    private static partial Regex AppVersion();
}
