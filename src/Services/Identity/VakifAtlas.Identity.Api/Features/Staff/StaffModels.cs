namespace VakifAtlas.Identity.Api.Features.Staff;

public sealed record StaffLoginRequest(string? Email, string? Password);

/// <summary>
/// Şifre doğrulandı; ikinci adım bekleniyor. <c>mfa</c>: <c>required</c> (Authenticator kodu)
/// veya <c>enrollmentRequired</c> (ilk giriş: TOTP kurulumu).
/// </summary>
public sealed record MfaChallengeResponse(string Mfa, string MfaToken, int ExpiresIn);

public sealed record MfaTokenRequest(string? MfaToken);

/// <summary>Kod veya kurtarma kodundan biri.</summary>
public sealed record MfaVerifyRequest(string? MfaToken, string? Code, string? RecoveryCode);

public sealed record MfaConfirmRequest(string? MfaToken, string? Code);

/// <summary><c>secret</c>: elle girilecek Base32 sır; <c>otpauthUri</c>: QR içeriği.</summary>
public sealed record TotpEnrollmentResponse(string Secret, string OtpauthUri, string Issuer, string Account);

public sealed record TotpCodeRequest(string? Code);

public sealed record StaffPasswordChangeRequest(string? CurrentPassword, string? NewPassword);

public sealed record StaffUserResponse(
    string Id,
    string DisplayName,
    string Email,
    IReadOnlyList<string> Roles,
    DateTimeOffset? MfaEnrolledAt,
    DateTimeOffset? LastLoginAt,
    int RecoveryCodesRemaining);

/// <summary><c>recoveryCodes</c> yalnızca kurulum/yenilemede bir kez döner (sunucuda yalnızca özetleri kalır).</summary>
public sealed record StaffSessionResponse(
    string AccessToken,
    string RefreshToken,
    int ExpiresIn,
    StaffUserResponse User,
    IReadOnlyList<string>? RecoveryCodes);

public sealed record RecoveryCodesResponse(IReadOnlyList<string> RecoveryCodes);

// Personel yönetimi (vgm_admin)

public sealed record StaffMemberResponse(
    string Id,
    string DisplayName,
    string Email,
    IReadOnlyList<string> Roles,
    bool Disabled,
    DateTimeOffset? MfaEnrolledAt,
    DateTimeOffset? LastLoginAt,
    DateTimeOffset CreatedAt);

public sealed record InviteStaffRequest(string? Email, string? DisplayName, IReadOnlyList<string>? Roles);

public sealed record StaffRolesRequest(IReadOnlyList<string>? Roles);

public sealed record AuditEntryResponse(
    string Id,
    DateTimeOffset At,
    AuditActor Actor,
    string Service,
    string Action,
    string TargetType,
    string TargetId,
    IReadOnlyDictionary<string, string?> Changes,
    string? CorrelationId);

/// <summary>İşlemi yapan personel; hesap silinmişse <c>displayName</c> <c>null</c>.</summary>
public sealed record AuditActor(string Id, string? DisplayName);
