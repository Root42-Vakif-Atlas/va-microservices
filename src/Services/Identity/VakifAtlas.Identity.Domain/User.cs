namespace VakifAtlas.Identity.Domain;

/// <summary>
/// Hesap (vatandaş; personel rolü olan hesap aynı zamanda VGM personelidir). E-posta kanonik
/// (küçük harf) biçimde saklanır.
/// </summary>
public sealed class User
{
    public const int MaxFailedLogins = 5;

    public static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(15);

    public required string Id { get; init; }

    public required string Email { get; init; }

    public required string DisplayName { get; set; }

    public required string PasswordHash { get; set; }

    public bool EmailVerified { get; set; }

    public int FailedLoginAttempts { get; private set; }

    public DateTimeOffset? LockedUntil { get; private set; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Onay anındaki yasal metin sürümleri (denetim izi).</summary>
    public required string TermsVersion { get; set; }

    public required string PrivacyNoticeVersion { get; set; }

    public DateTimeOffset ConsentedAt { get; set; }

    public List<string> Roles { get; set; } = [];

    /// <summary>TOTP sırrı (şifreli); <c>null</c> → personel ilk girişte kurulum yapar.</summary>
    public string? MfaSecret { get; set; }

    public DateTimeOffset? MfaEnrolledAt { get; set; }

    /// <summary>Son kabul edilen TOTP adımı (aynı kodun tekrar kullanımına karşı).</summary>
    public long? LastTotpStep { get; set; }

    /// <summary>Personel erişimi devre dışı (vatandaş hesabı etkilenmez).</summary>
    public DateTimeOffset? StaffDisabledAt { get; set; }

    public DateTimeOffset? LastStaffLoginAt { get; set; }

    public bool HasAnyRole(IEnumerable<string> roles) => Roles.Intersect(roles, StringComparer.Ordinal).Any();

    public void ResetMfa()
    {
        MfaSecret = null;
        MfaEnrolledAt = null;
        LastTotpStep = null;
    }

    /// <summary>
    /// Doğrulanmamış hesaba yeniden kayıt: son kaydın adı, şifresi ve onayları geçerli olur.
    /// </summary>
    public void ReplacePendingRegistration(string displayName, string passwordHash, string termsVersion,
        string privacyNoticeVersion, DateTimeOffset consentedAt)
    {
        if (EmailVerified)
        {
            throw new InvalidOperationException("Doğrulanmış hesabın kaydı değiştirilemez.");
        }

        DisplayName = displayName;
        PasswordHash = passwordHash;
        TermsVersion = termsVersion;
        PrivacyNoticeVersion = privacyNoticeVersion;
        ConsentedAt = consentedAt;
    }

    /// <summary>Kilitliyse kalan süre.</summary>
    public TimeSpan? LockRemaining(DateTimeOffset now) =>
        LockedUntil is { } until && until > now ? until - now : null;

    /// <summary>Hatalı giriş; sınırda hesap <see cref="LockDuration"/> kilitlenir.</summary>
    public void RecordFailedLogin(DateTimeOffset now)
    {
        FailedLoginAttempts++;
        if (FailedLoginAttempts >= MaxFailedLogins)
        {
            FailedLoginAttempts = 0;
            LockedUntil = now + LockDuration;
        }
    }

    public void ClearLock()
    {
        FailedLoginAttempts = 0;
        LockedUntil = null;
    }
}
