using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using VakifAtlas.Identity.Domain;

namespace VakifAtlas.Identity.Api.Infrastructure;

public sealed class MfaOptions
{
    public const string Section = "Identity:Mfa";

    /// <summary>
    /// TOTP sırlarını şifreleyen AES-256 anahtarı (base64, 32 bayt). Veritabanı sızıntısında
    /// sırlar açığa çıkmaz. Prod'da Kubernetes Secret'tan gelir; tüm replikalarda aynıdır.
    /// </summary>
    public string EncryptionKey { get; set; } = string.Empty;

    /// <summary>Authenticator uygulamasında görünen hesap sağlayıcısı.</summary>
    public string Issuer { get; set; } = "Vakıf Atlas Yönetim";
}

/// <summary>TOTP sırrı şifreleme (AES-GCM, rastgele nonce): <c>v1.&lt;nonce&gt;.&lt;şifreli+etiket&gt;</c>.</summary>
public sealed class MfaSecretProtector
{
    private const string Version = "v1";
    private readonly byte[] _key;

    public MfaSecretProtector(IOptions<MfaOptions> options)
    {
        var configured = options.Value.EncryptionKey;
        _key = string.IsNullOrEmpty(configured)
            ? throw new InvalidOperationException("Identity:Mfa:EncryptionKey is required.")
            : Convert.FromBase64String(configured);
        if (_key.Length != 32)
        {
            throw new InvalidOperationException("Identity:Mfa:EncryptionKey must be 32 bytes (base64).");
        }
    }

    public string Protect(byte[] secret)
    {
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var cipher = new byte[secret.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        using var aes = new AesGcm(_key, tag.Length);
        aes.Encrypt(nonce, secret, cipher, tag, Encoding.ASCII.GetBytes(Version));
        return $"{Version}.{Convert.ToBase64String(nonce)}.{Convert.ToBase64String([.. cipher, .. tag])}";
    }

    public byte[] Unprotect(string protectedSecret)
    {
        var parts = protectedSecret.Split('.');
        if (parts.Length != 3 || parts[0] != Version)
        {
            throw new CryptographicException("Unknown MFA secret format.");
        }

        var nonce = Convert.FromBase64String(parts[1]);
        var payload = Convert.FromBase64String(parts[2]);
        var tagLength = AesGcm.TagByteSizes.MaxSize;
        var secret = new byte[payload.Length - tagLength];
        using var aes = new AesGcm(_key, tagLength);
        aes.Decrypt(nonce, payload.AsSpan(0, secret.Length), payload.AsSpan(secret.Length), secret, Encoding.ASCII.GetBytes(Version));
        return secret;
    }
}

/// <summary>Kurtarma kodları: <c>XXXX-XXXX</c> (Base32 alfabesi), HMAC özetiyle saklanır.</summary>
public static class RecoveryCodes
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static string New()
    {
        Span<char> chars = stackalloc char[9];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = i == 4 ? '-' : Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return new string(chars);
    }

    public static string Normalize(string code) =>
        new string(code.Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant).ToArray()) is { Length: 8 } clean
            ? $"{clean[..4]}-{clean[4..]}"
            : string.Empty;

    public static string Hash(SecretTokens secrets, string userId, string code) => secrets.HashCode($"recovery:{userId}", code);

    public static IReadOnlyList<(string Code, RecoveryCode Entity)> Generate(SecretTokens secrets, string userId,
        DateTimeOffset now) =>
        Enumerable.Range(0, RecoveryCode.Count).Select(_ =>
        {
            var code = New();
            return (code, new RecoveryCode
            {
                Id = Guid.CreateVersion7(now),
                UserId = userId,
                CodeHash = Hash(secrets, userId, code),
                CreatedAt = now,
            });
        }).ToArray();
}
