using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using VakifAtlas.Identity.Api.Infrastructure;
using VakifAtlas.Identity.Domain;
using VakifAtlas.ServiceDefaults.Redis;

namespace VakifAtlas.Identity.Api.Features.Auth;

/// <summary>Kod üretimi/gönderimi ve şifre kuralları; uç noktalar arasında ortak.</summary>
public sealed class AuthWorkflow(
    IdentityDbContext db,
    SecretTokens secrets,
    IUsageLimiter limiter,
    PasswordPolicy policy,
    TimeProvider clock)
{
    public static readonly ChallengeResponse Challenge =
        new((int)OneTimeCode.ResendCooldown.TotalSeconds, Credentials.CodeLength);

    /// <summary>
    /// E-posta + amaç başına 60 sn bekleme ve günlük üst sınır; hesap olmasa da uygulanır
    /// (numaralandırma koruması).
    /// </summary>
    public async Task<UsageDecision> TryStartCooldownAsync(string email, CodePurpose purpose, CancellationToken cancellationToken)
    {
        var key = CooldownKey(email, purpose);
        var cooldown = await limiter.TryStartCooldownAsync(key, OneTimeCode.ResendCooldown, cancellationToken);
        if (!cooldown.Allowed)
        {
            return cooldown;
        }

        return await limiter.TryConsumeAsync($"{key}:day", OneTimeCode.MaxCodesPerDay, TimeSpan.FromDays(1), cancellationToken);
    }

    /// <summary>Yeni kod üretir ve e-posta outbox'ına ekler (kayıt çağıranın <c>SaveChangesAsync</c>'iyle).</summary>
    public async Task IssueCodeAsync(string email, CodePurpose purpose, string language, CancellationToken cancellationToken)
    {
        var code = SecretTokens.NewCode();
        var hash = secrets.HashCode(email, code);
        var now = clock.GetUtcNow();
        var existing = await db.Codes.FindAsync([email, purpose], cancellationToken);
        if (existing is null)
        {
            db.Codes.Add(new OneTimeCode { Email = email, Purpose = purpose, CodeHash = hash, ExpiresAt = now + OneTimeCode.Lifetime });
        }
        else
        {
            existing.Reissue(hash, now);
        }

        QueueEmail(email, purpose == CodePurpose.VerifyEmail ? EmailKind.VerificationCode : EmailKind.PasswordResetCode,
            language, code);
    }

    public void QueueEmail(string email, EmailKind kind, string language, string? code = null) =>
        db.Emails.Add(new EmailMessage
        {
            Id = Guid.CreateVersion7(clock.GetUtcNow()),
            To = email,
            Kind = kind,
            Language = language,
            Code = code,
            CreatedAt = clock.GetUtcNow(),
        });

    public async Task<(CodeCheckOutcome Outcome, OneTimeCode? Code)> CheckCodeAsync(string email, CodePurpose purpose,
        string code, CancellationToken cancellationToken)
    {
        // Deneme hakkı tek bir koşullu UPDATE ile ayrılır: eşzamanlı istekler aynı sayacı okuyup
        // sınırı aşamaz (oku-değiştir-yaz yarışı yok).
        var now = clock.GetUtcNow();
        var reserved = await db.Codes
            .Where(c => c.Email == email && c.Purpose == purpose && c.ExpiresAt > now && c.Attempts < OneTimeCode.MaxAttempts)
            .ExecuteUpdateAsync(set => set.SetProperty(c => c.Attempts, c => c.Attempts + 1), cancellationToken);
        if (reserved == 0)
        {
            return (CodeCheckOutcome.Expired, null);
        }

        var stored = await db.Codes.SingleAsync(c => c.Email == email && c.Purpose == purpose, cancellationToken);
        await db.Entry(stored).ReloadAsync(cancellationToken);
        var normalized = Credentials.NormalizeCode(code);
        var outcome = stored.CheckReserved(hash => secrets.CodeMatches(email, normalized, hash));
        return (outcome, stored);
    }

    /// <summary>Yalnızca teknik sınırlar (<see cref="PasswordPolicy"/>); şifre gücü denetlenmez.</summary>
    public Task<bool> IsAcceptablePasswordAsync(string password, string email, string displayName,
        CancellationToken cancellationToken) =>
        Task.FromResult(policy.Validate(password) is null);

    public static UserResponse ToResponse(User user) => new(user.Id, user.DisplayName, user.Email);

    public static SessionResponse ToSession(TokenPair tokens, User user) =>
        new(tokens.AccessToken, tokens.RefreshToken, tokens.ExpiresIn, ToResponse(user));

    public static TokensResponse ToTokens(TokenPair tokens) => new(tokens.AccessToken, tokens.RefreshToken, tokens.ExpiresIn);

    /// <summary>Redis anahtarında e-posta yer almaz.</summary>
    internal static string CooldownKey(string email, CodePurpose purpose) => $"auth-code:{purpose}:{EmailKey(email)}";

    private static string EmailKey(string email) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(email)))[..32];

    /// <summary>
    /// Hatalı şifre veya MFA kodu: sayaç atomik artar, sınırda hesap kilitlenir (eşzamanlı
    /// denemeler aynı sayacı okuyup kilidi atlayamaz; <see cref="User.RecordFailedLogin"/> ile aynı kural).
    /// </summary>
    public Task RecordFailedLoginAsync(string userId, CancellationToken cancellationToken)
    {
        var lockUntil = clock.GetUtcNow() + User.LockDuration;
        return db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(set => set
            .SetProperty(u => u.LockedUntil,
                u => u.FailedLoginAttempts + 1 >= User.MaxFailedLogins ? lockUntil : u.LockedUntil)
            .SetProperty(u => u.FailedLoginAttempts,
                u => u.FailedLoginAttempts + 1 >= User.MaxFailedLogins ? 0 : u.FailedLoginAttempts + 1),
            cancellationToken);
    }

    public Task<User?> FindByEmailAsync(string email, CancellationToken cancellationToken) =>
        db.Users.SingleOrDefaultAsync(u => u.Email == email, cancellationToken);
}
