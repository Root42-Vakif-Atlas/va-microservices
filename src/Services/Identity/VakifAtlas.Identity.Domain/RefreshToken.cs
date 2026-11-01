namespace VakifAtlas.Identity.Domain;

/// <summary>
/// Opak, tek kullanımlık yenileme anahtarı (yalnızca SHA-256 özeti saklanır).
/// Bir oturum = bir aile (<see cref="FamilyId"/> = erişim anahtarındaki <c>sid</c>).
/// Kullanılmış anahtarın tekrar sunulması çalınma belirtisidir: aile iptal edilir.
/// </summary>
public sealed class RefreshToken
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    /// <summary>Personel oturumu: 30 dk işlem yapılmazsa yenileme anahtarı geçersizleşir.</summary>
    public static readonly TimeSpan StaffIdleLifetime = TimeSpan.FromMinutes(30);

    /// <summary>Personel oturumu en çok bir iş günü sürer (yeniden şifre + MFA).</summary>
    public static readonly TimeSpan StaffSessionLifetime = TimeSpan.FromHours(12);

    public Guid Id { get; init; }

    public required string UserId { get; init; }

    public Guid FamilyId { get; init; }

    public required string TokenHash { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset ExpiresAt { get; init; }

    public DateTimeOffset? ConsumedAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public SessionKind Kind { get; init; }

    /// <summary>Oturumun mutlak bitişi (personel); yenilemede aileyle taşınır.</summary>
    public DateTimeOffset? SessionExpiresAt { get; init; }

    public bool IsUsable(DateTimeOffset now) => ConsumedAt is null && RevokedAt is null && ExpiresAt > now;
}

public enum SessionKind
{
    Citizen,

    /// <summary>VGM Yönetim Paneli oturumu (ayrı audience, MFA ile açılır).</summary>
    Staff,
}
