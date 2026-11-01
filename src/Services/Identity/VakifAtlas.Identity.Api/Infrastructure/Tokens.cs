using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using VakifAtlas.Identity.Domain;
using VakifAtlas.ServiceDefaults.Auth;

namespace VakifAtlas.Identity.Api.Infrastructure;

public sealed class SigningOptions
{
    public const string Section = "Identity:Signing";

    /// <summary>
    /// ES256 (P-256) özel anahtarları, PEM. İlki imzalar; diğerleri rotasyon sırasında
    /// yalnızca doğrulama için JWKS'te yayınlanır. Prod'da Kubernetes Secret'tan gelir.
    /// </summary>
    public List<SigningKeyConfig> Keys { get; set; } = [];

    /// <summary>Anahtar verilmemişse üretip veritabanında sakla (yalnızca dev/test).</summary>
    public bool AllowGeneratedKey { get; set; }

    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Personel erişim anahtarı daha kısa yaşar (ayrıcalıklı oturum).</summary>
    public TimeSpan StaffAccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(10);
}

public sealed class SigningKeyConfig
{
    public string KeyId { get; set; } = string.Empty;

    public string PrivateKeyPem { get; set; } = string.Empty;
}

/// <summary>Dev/test'te üretilen imza anahtarı (replikalar arasında paylaşılır).</summary>
public sealed class StoredSigningKey
{
    public required string KeyId { get; init; }

    public required string PrivateKeyPem { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>İmza anahtarları; açılışta (migration sonrası) yüklenir.</summary>
public sealed class SigningKeyStore : IDisposable
{
    private readonly List<ECDsa> _algorithms = [];

    public IReadOnlyList<ECDsaSecurityKey> Keys { get; private set; } = [];

    public SigningCredentials Current => new(Keys[0], SecurityAlgorithms.EcdsaSha256);

    public void Load(IEnumerable<(string KeyId, string Pem)> keys)
    {
        var loaded = new List<ECDsaSecurityKey>();
        foreach (var (keyId, pem) in keys)
        {
            var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(pem);
            if (ecdsa.KeySize != 256)
            {
                throw new InvalidOperationException($"Signing key {keyId} must be P-256.");
            }

            _algorithms.Add(ecdsa);
            loaded.Add(new ECDsaSecurityKey(ecdsa) { KeyId = keyId });
        }

        Keys = loaded.Count > 0 ? loaded : throw new InvalidOperationException("No signing keys configured.");
    }

    /// <summary>JWKS: yalnızca açık anahtarlar.</summary>
    public IReadOnlyList<JsonWebKey> PublicJwks() => Keys.Select(key =>
    {
        var parameters = key.ECDsa.ExportParameters(includePrivateParameters: false);
        using var publicKey = ECDsa.Create(parameters);
        var jwk = JsonWebKeyConverter.ConvertFromECDsaSecurityKey(new ECDsaSecurityKey(publicKey) { KeyId = key.KeyId });
        jwk.Use = "sig";
        jwk.Alg = SecurityAlgorithms.EcdsaSha256;
        return jwk;
    }).ToArray();

    public void Dispose()
    {
        foreach (var algorithm in _algorithms)
        {
            algorithm.Dispose();
        }
    }
}

internal sealed class SigningKeyInitializer(
    IServiceScopeFactory scopes,
    SigningKeyStore store,
    IOptions<SigningOptions> options,
    TimeProvider clock) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var configured = options.Value.Keys.Where(k => !string.IsNullOrWhiteSpace(k.PrivateKeyPem)).ToList();
        if (configured.Count > 0)
        {
            store.Load(configured.Select(k => (k.KeyId, k.PrivateKeyPem)));
            return;
        }

        if (!options.Value.AllowGeneratedKey)
        {
            throw new InvalidOperationException("Identity:Signing:Keys must be configured (generated keys are disabled).");
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var stored = await db.SigningKeys.OrderBy(k => k.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (stored is null)
        {
            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            stored = new StoredSigningKey
            {
                KeyId = $"dev-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}",
                PrivateKeyPem = ecdsa.ExportPkcs8PrivateKeyPem(),
                CreatedAt = clock.GetUtcNow(),
            };
            db.SigningKeys.Add(stored);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // Başka replika aynı anda üretti; onunkini kullan.
                db.ChangeTracker.Clear();
                stored = await db.SigningKeys.OrderBy(k => k.CreatedAt).FirstAsync(cancellationToken);
            }
        }

        store.Load([(stored.KeyId, stored.PrivateKeyPem)]);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed record TokenPair(string AccessToken, string RefreshToken, int ExpiresIn);

/// <summary>
/// Erişim (JWT) + yenileme (opak, tek kullanımlık) anahtarları. Vatandaş: 15 dk / 30 gün,
/// rol taşımaz. Personel: ayrı audience, rol + <c>amr=["pwd","otp"]</c>, 10 dk erişim, 30 dk
/// hareketsizlik ve 12 saat mutlak oturum sınırı.
/// </summary>
public sealed class TokenService(
    SigningKeyStore keys,
    IdentityDbContext db,
    IOptions<AuthOptions> auth,
    IOptions<SigningOptions> signing,
    TimeProvider clock)
{
    private static readonly JsonWebTokenHandler Handler = new();

    /// <summary>Yeni oturum (yeni aile) açar. Kayıt çağıranın <c>SaveChangesAsync</c>'iyle kalıcılaşır.</summary>
    public TokenPair StartSession(User user) => Issue(user, Guid.NewGuid(), SessionKind.Citizen, null);

    /// <summary>Şifre + TOTP doğrulandıktan sonra personel oturumu.</summary>
    public TokenPair StartStaffSession(User user) =>
        Issue(user, Guid.NewGuid(), SessionKind.Staff, clock.GetUtcNow() + RefreshToken.StaffSessionLifetime);

    public TokenPair Issue(User user, Guid familyId, SessionKind kind, DateTimeOffset? sessionExpiresAt)
    {
        var now = clock.GetUtcNow();
        var staff = kind == SessionKind.Staff;
        var refreshToken = SecretTokens.NewRefreshToken();
        var refreshExpires = staff
            ? Min(now + RefreshToken.StaffIdleLifetime, sessionExpiresAt ?? now + RefreshToken.StaffSessionLifetime)
            : now + RefreshToken.Lifetime;
        db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.CreateVersion7(now),
            UserId = user.Id,
            FamilyId = familyId,
            TokenHash = SecretTokens.HashRefreshToken(refreshToken),
            CreatedAt = now,
            ExpiresAt = refreshExpires,
            Kind = kind,
            SessionExpiresAt = sessionExpiresAt,
        });

        var lifetime = staff ? signing.Value.StaffAccessTokenLifetime : signing.Value.AccessTokenLifetime;
        var claims = new Dictionary<string, object>
        {
            [VakifAtlasClaims.Subject] = user.Id,
            [VakifAtlasClaims.Name] = user.DisplayName,
            [VakifAtlasClaims.SessionId] = familyId.ToString("N"),
            [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString("N"),
        };
        if (staff)
        {
            claims[VakifAtlasClaims.Roles] = user.Roles.Where(StaffRoles.IsStaffRole).ToArray();
            claims[VakifAtlasClaims.AuthenticationMethods] = new[] { "pwd", VakifAtlasClaims.OneTimePasswordMethod };
        }

        var accessToken = Handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = auth.Value.Issuer,
            Audience = staff ? auth.Value.AdminAudience : auth.Value.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = (now + lifetime).UtcDateTime,
            Claims = claims,
            SigningCredentials = keys.Current,
        });

        return new TokenPair(accessToken, refreshToken, (int)lifetime.TotalSeconds);
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}
