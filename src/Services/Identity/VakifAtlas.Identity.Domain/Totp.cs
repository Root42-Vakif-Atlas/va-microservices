using System.Security.Cryptography;
using System.Text;

namespace VakifAtlas.Identity.Domain;

/// <summary>
/// RFC 6238 TOTP (HMAC-SHA1, 6 hane, 30 sn adım): Authenticator uygulamalarının ortak
/// varsayılanı. Saat kaymasına karşı ±1 adım kabul edilir; aynı adımın kodu ikinci kez
/// kabul edilmez (son kullanılan adım hesapta saklanır).
/// </summary>
public static class Totp
{
    public const int Digits = 6;
    public const int StepSeconds = 30;
    public const int SecretBytes = 20;
    public const int AllowedDrift = 1;

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(SecretBytes);

    public static long StepAt(DateTimeOffset time) => time.ToUnixTimeSeconds() / StepSeconds;

    public static string Code(byte[] secret, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counter, step);
        Span<byte> hash = stackalloc byte[20];
        // RFC 6238 varsayılanı; Authenticator uygulamalarının çoğu yalnızca SHA-1'i destekler.
        // HMAC-SHA1 (çarpışma direncine dayanmaz) TOTP için güvenli kabul edilir (RFC 4226 §B).
#pragma warning disable CA5350
        HMACSHA1.HashData(secret, counter, hash);
#pragma warning restore CA5350
        var offset = hash[^1] & 0x0f;
        var binary = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Kodun geçerli olduğu adım; geçersizse veya <paramref name="lastUsedStep"/>'ten yeni değilse <c>null</c>.
    /// </summary>
    public static long? Match(byte[] secret, string code, DateTimeOffset now, long? lastUsedStep)
    {
        var normalized = new string(code.Where(char.IsAsciiDigit).ToArray());
        if (normalized.Length != Digits)
        {
            return null;
        }

        var current = StepAt(now);
        for (var step = current - AllowedDrift; step <= current + AllowedDrift; step++)
        {
            if (lastUsedStep is { } last && step <= last)
            {
                continue;
            }

            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(secret, step)),
                    Encoding.ASCII.GetBytes(normalized)))
            {
                return step;
            }
        }

        return null;
    }

    /// <summary>Authenticator uygulamasına eklemek için <c>otpauth://</c> adresi (QR içeriği).</summary>
    public static string ProvisioningUri(byte[] secret, string issuer, string account) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}" +
        $"?secret={Base32.Encode(secret)}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={Digits}&period={StepSeconds}";
}

/// <summary>RFC 4648 Base32 (dolgusuz, büyük harf): TOTP sırrının elle girilen biçimi.</summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var builder = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var value in data)
        {
            buffer = (buffer << 8) | value;
            bits += 8;
            while (bits >= 5)
            {
                builder.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            builder.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return builder.ToString();
    }

    public static byte[] Decode(string text)
    {
        var clean = text.Replace(" ", string.Empty, StringComparison.Ordinal).TrimEnd('=').ToUpperInvariant();
        var output = new List<byte>(clean.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var character in clean)
        {
            var index = Alphabet.IndexOf(character, StringComparison.Ordinal);
            if (index < 0)
            {
                throw new FormatException("Invalid Base32 character.");
            }

            buffer = (buffer << 5) | index;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)((buffer >> (bits - 8)) & 0xff));
                bits -= 8;
            }
        }

        return [.. output];
    }
}
