using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace VakifAtlas.Identity.Api.Infrastructure;

/// <summary>
/// Argon2id (OWASP: m=19 MiB, t=2, p=1), kullanıcıya özgü 16 baytlık tuz. Biçim PHC:
/// <c>$argon2id$v=19$m=19456,t=2,p=1$&lt;tuz&gt;$&lt;özet&gt;</c>; parametreler kayıtla
/// saklandığından ileride artırılabilir.
/// </summary>
public sealed class PasswordHasher
{
    private const int MemoryKib = 19_456;
    private const int Iterations = 2;
    private const int Parallelism = 1;
    private const int HashBytes = 32;

    /// <summary>
    /// Her özet ~19 MiB bellek kullanır; eşzamanlı giriş patlamasında belleği sınırlamak
    /// için aynı anda en çok çekirdek sayısı × 2 hesap yapılır.
    /// </summary>
    private static readonly SemaphoreSlim Concurrency = new(Environment.ProcessorCount * 2);

    /// <summary>Bilinmeyen e-postada da özet hesaplanır (zamanlama eşitliği).</summary>
    private readonly Lazy<string> _dummyHash;

    public PasswordHasher() => _dummyHash = new Lazy<string>(() => Hash("timing-parity-placeholder"));

    public Task<string> HashAsync(string password, CancellationToken cancellationToken) =>
        Throttled(() => Hash(password), cancellationToken);

    public Task<bool> VerifyAsync(string password, string encoded, CancellationToken cancellationToken) =>
        Throttled(() => Verify(password, encoded), cancellationToken);

    public Task VerifyDummyAsync(string password, CancellationToken cancellationToken) =>
        Throttled(() => Verify(password, _dummyHash.Value), cancellationToken);

    private static async Task<T> Throttled<T>(Func<T> work, CancellationToken cancellationToken)
    {
        await Concurrency.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(work, cancellationToken);
        }
        finally
        {
            Concurrency.Release();
        }
    }

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Compute(password, salt, MemoryKib, Iterations, Parallelism);
        return $"$argon2id$v=19$m={MemoryKib},t={Iterations},p={Parallelism}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public bool Verify(string password, string encoded)
    {
        var parts = encoded.Split('$');
        if (parts.Length != 6 || parts[1] != "argon2id")
        {
            return false;
        }

        var parameters = parts[3].Split(',').Select(p => p.Split('=')).ToDictionary(p => p[0], p => int.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture));
        var salt = Convert.FromBase64String(parts[4]);
        var expected = Convert.FromBase64String(parts[5]);
        var actual = Compute(password, salt, parameters["m"], parameters["t"], parameters["p"]);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static byte[] Compute(string password, byte[] salt, int memory, int iterations, int parallelism)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memory,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
        };
        return argon.GetBytes(HashBytes);
    }
}

public sealed class IdentitySecretsOptions
{
    public const string Section = "Identity";

    /// <summary>
    /// Kod özetleri için gizli anahtar (HMAC). 6 haneli kodun düz SHA-256 özeti bir
    /// milyon denemeyle çözülebildiğinden, veritabanı sızıntısına karşı gereklidir.
    /// </summary>
    public string CodePepper { get; set; } = string.Empty;
}

/// <summary>Tek kullanımlık kodlar ve yenileme anahtarları için üretim/özet.</summary>
public sealed class SecretTokens(IOptions<IdentitySecretsOptions> options)
{
    private readonly byte[] _pepper = Encoding.UTF8.GetBytes(
        string.IsNullOrEmpty(options.Value.CodePepper)
            ? throw new InvalidOperationException("Identity:CodePepper is required.")
            : options.Value.CodePepper);

    public static string NewCode() =>
        RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);

    public string HashCode(string email, string code) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(_pepper, Encoding.UTF8.GetBytes($"{email}:{code}")));

    public bool CodeMatches(string email, string code, string storedHash) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(HashCode(email, code)),
            Encoding.ASCII.GetBytes(storedHash));

    public static string NewRefreshToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string HashRefreshToken(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
