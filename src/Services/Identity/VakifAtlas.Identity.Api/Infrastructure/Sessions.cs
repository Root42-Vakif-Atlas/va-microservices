using Microsoft.EntityFrameworkCore;
using VakifAtlas.Identity.Domain;
using VakifAtlas.ServiceDefaults.Auth;

namespace VakifAtlas.Identity.Api.Infrastructure;

public enum RefreshOutcome
{
    Rotated,
    Invalid,

    /// <summary>Kullanılmış anahtar tekrar sunuldu: aile (oturum) iptal edildi.</summary>
    ReuseDetected,
}

/// <summary>Oturum (yenileme anahtarı ailesi) yaşam döngüsü ve iptali.</summary>
public sealed class SessionService(
    IdentityDbContext db,
    TokenService tokens,
    ISessionRevocationStore revocations,
    TimeProvider clock)
{
    public async Task<(RefreshOutcome Outcome, TokenPair? Tokens)> RotateAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var hash = SecretTokens.HashRefreshToken(refreshToken);
        var token = await db.RefreshTokens.SingleOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
        var now = clock.GetUtcNow();
        if (token is null || token.RevokedAt is not null || token.ExpiresAt <= now)
        {
            return (RefreshOutcome.Invalid, null);
        }

        // Tüketim ve yeni anahtarın kaydı tek işlemdedir: kayıt başarısız olursa eski anahtar
        // tüketilmiş kalıp kullanıcının oturumu düşmez.
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            // Atomik tüketim: eşzamanlı iki yenilemeden yalnızca biri kazanır (ikincisi satır
            // kilidini bekler, işlem bitince koşul artık tutmaz).
            var consumed = await db.RefreshTokens
                .Where(t => t.Id == token.Id && t.ConsumedAt == null)
                .ExecuteUpdateAsync(setters => setters.SetProperty(t => t.ConsumedAt, now), cancellationToken);
            if (consumed == 1)
            {
                var user = await db.Users.SingleOrDefaultAsync(u => u.Id == token.UserId, cancellationToken);
                if (user is null || (token.Kind == SessionKind.Staff && !StaffAccess.IsAllowed(user)))
                {
                    return (RefreshOutcome.Invalid, null);
                }

                var pair = tokens.Issue(user, token.FamilyId, token.Kind, token.SessionExpiresAt);
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return (RefreshOutcome.Rotated, pair);
            }
        }

        await RevokeFamiliesAsync(token.UserId, [token.FamilyId], cancellationToken);
        return (RefreshOutcome.ReuseDetected, null);
    }

    /// <summary>Çıkış: anahtarın ailesi ve o oturumun erişim anahtarı iptal edilir.</summary>
    public async Task LogoutAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var hash = SecretTokens.HashRefreshToken(refreshToken);
        var token = await db.RefreshTokens.AsNoTracking().SingleOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
        if (token is not null)
        {
            await RevokeFamiliesAsync(token.UserId, [token.FamilyId], cancellationToken);
        }
    }

    /// <summary>Kullanıcının tüm oturumları (şifre sıfırlama, hesap silme) veya biri hariç hepsi (şifre değişimi).</summary>
    public async Task RevokeAllAsync(string userId, CancellationToken cancellationToken)
    {
        var families = await db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null)
            .Select(t => t.FamilyId).Distinct().ToListAsync(cancellationToken);
        await RevokeFamiliesAsync(userId, families, cancellationToken);
    }

    /// <summary>Yalnızca personel oturumları (rol azaltma, devre dışı bırakma, MFA sıfırlama).</summary>
    public async Task RevokeStaffSessionsAsync(string userId, CancellationToken cancellationToken)
    {
        var families = await db.RefreshTokens
            .Where(t => t.UserId == userId && t.Kind == SessionKind.Staff && t.RevokedAt == null)
            .Select(t => t.FamilyId).Distinct().ToListAsync(cancellationToken);
        await RevokeFamiliesAsync(userId, families, cancellationToken);
    }

    private async Task RevokeFamiliesAsync(string userId, IReadOnlyCollection<Guid> families, CancellationToken cancellationToken)
    {
        if (families.Count == 0)
        {
            return;
        }

        var now = clock.GetUtcNow();
        await db.RefreshTokens
            .Where(t => t.UserId == userId && families.Contains(t.FamilyId) && t.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(t => t.RevokedAt, now), cancellationToken);
        await revocations.RevokeAsync(families.Select(f => f.ToString("N")), cancellationToken);
    }

}

/// <summary>Personel girişinin ön koşulu: personel rolü, erişim devre dışı değil, e-posta doğrulanmış.</summary>
public static class StaffAccess
{
    public static bool IsAllowed(User user) =>
        user.EmailVerified && user.StaffDisabledAt is null && user.HasAnyRole(StaffRoles.All);
}
