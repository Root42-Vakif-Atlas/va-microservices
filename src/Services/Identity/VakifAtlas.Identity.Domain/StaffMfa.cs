namespace VakifAtlas.Identity.Domain;

public enum MfaChallengePurpose
{
    /// <summary>Kayıtlı Authenticator koduyla giriş.</summary>
    Verify,

    /// <summary>İlk giriş (veya sıfırlama sonrası): yeni TOTP sırrının kurulumu.</summary>
    Enroll,
}

/// <summary>
/// Personel girişinin ikinci adımı: şifre doğrulandıktan sonra verilen kısa ömürlü, tek
/// kullanımlık bilet (<c>mfaToken</c>; yalnızca özeti saklanır). Bilet başına en çok
/// <see cref="MaxAttempts"/> kod denemesi.
/// </summary>
public sealed class MfaChallenge
{
    public const int MaxAttempts = 5;

    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    public Guid Id { get; init; }

    public required string UserId { get; init; }

    public required string TokenHash { get; init; }

    public MfaChallengePurpose Purpose { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset ExpiresAt { get; init; }

    public int Attempts { get; set; }

    /// <summary>Kurulumda üretilen, henüz onaylanmamış sır (şifreli).</summary>
    public string? PendingSecret { get; set; }

    public DateTimeOffset? ConsumedAt { get; set; }

    public bool IsUsable(DateTimeOffset now) => ConsumedAt is null && ExpiresAt > now && Attempts < MaxAttempts;
}

/// <summary>Authenticator erişimi kaybolursa tek kullanımlık kurtarma kodu (yalnızca özeti saklanır).</summary>
public sealed class RecoveryCode
{
    public const int Count = 10;

    public Guid Id { get; init; }

    public required string UserId { get; init; }

    public required string CodeHash { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? UsedAt { get; set; }
}

/// <summary>Merkezi denetim kaydı satırı (tüm servislerin personel işlemleri).</summary>
public sealed class AuditEntry
{
    public Guid Id { get; init; }

    public DateTimeOffset At { get; init; }

    public required string ActorId { get; init; }

    public required string Service { get; init; }

    public required string Action { get; init; }

    public required string TargetType { get; init; }

    public required string TargetId { get; init; }

    public Dictionary<string, string?> Changes { get; init; } = [];

    public string? CorrelationId { get; init; }
}
