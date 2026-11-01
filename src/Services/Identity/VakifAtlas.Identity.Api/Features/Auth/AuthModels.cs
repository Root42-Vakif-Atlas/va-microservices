namespace VakifAtlas.Identity.Api.Features.Auth;

public sealed record LoginRequest(string? Email, string? Password);

public sealed record RegisterRequest(string? DisplayName, string? Email, string? Password, ConsentsRequest? Consents);

public sealed record ConsentsRequest(bool AcceptedTerms, bool AcknowledgedPrivacyNotice);

/// <summary>
/// <c>Password</c>: kodu giren kişinin şifresi; doğrulamada hesabın şifresi olur. Kod e-postanın
/// sahibine gittiğinden, adresi önceden kaydeden başkası hesabı kendi şifresiyle ele geçiremez.
/// </summary>
public sealed record VerifyEmailRequest(string? Email, string? Code, string? Password);

public sealed record EmailRequest(string? Email);

public sealed record RefreshRequest(string? RefreshToken);

public sealed record ResetPasswordRequest(string? Email, string? Code, string? NewPassword);

public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

public sealed record DeleteAccountRequest(string? Password);

public sealed record UserResponse(string Id, string DisplayName, string Email);

public sealed record TokensResponse(string AccessToken, string RefreshToken, int ExpiresIn);

public sealed record SessionResponse(string AccessToken, string RefreshToken, int ExpiresIn, UserResponse User);

/// <summary>Kod gönderildi (veya numaralandırma koruması gereği gönderilmiş gibi yanıtlandı).</summary>
public sealed record ChallengeResponse(int ResendAfter, int CodeLength);
