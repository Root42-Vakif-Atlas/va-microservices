namespace VakifAtlas.Identity.Domain;

public enum CodePurpose
{
    VerifyEmail,
    ResetPassword,
}

public enum CodeCheckOutcome
{
    Valid,
    Invalid,
    Expired,
}

/// <summary>
/// E-posta doğrulama / şifre sıfırlama kodu: 6 hane, yalnızca özeti saklanır,
/// 10 dk geçerli, en fazla 5 deneme (sonra <see cref="CodeCheckOutcome.Expired"/>).
/// </summary>
public sealed class OneTimeCode
{
    public const int MaxAttempts = 5;

    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan ResendCooldown = TimeSpan.FromSeconds(60);

    /// <summary>
    /// E-posta + amaç başına günde en çok bu kadar kod. Her yeniden gönderim deneme sayacını
    /// sıfırladığından sınırsız gönderim kaba kuvvete kapı açardı (5 × 1440 tahmin/gün).
    /// </summary>
    public const int MaxCodesPerDay = 10;

    public required string Email { get; init; }

    public CodePurpose Purpose { get; init; }

    public required string CodeHash { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public int Attempts { get; private set; }

    /// <param name="matches">Sabit süreli özet karşılaştırması (altyapıda).</param>
    public CodeCheckOutcome Check(Func<string, bool> matches, DateTimeOffset now)
    {
        if (now >= ExpiresAt || Attempts >= MaxAttempts)
        {
            return CodeCheckOutcome.Expired;
        }

        if (matches(CodeHash))
        {
            return CodeCheckOutcome.Valid;
        }

        Attempts++;
        return Attempts >= MaxAttempts ? CodeCheckOutcome.Expired : CodeCheckOutcome.Invalid;
    }

    /// <summary>
    /// Deneme hakkı veritabanında atomik olarak önceden ayrılmış (<see cref="Attempts"/> zaten
    /// artırılmış) koşulda sonucu verir; eşzamanlı istekler sınırı aşamaz.
    /// </summary>
    public CodeCheckOutcome CheckReserved(Func<string, bool> matches)
    {
        if (matches(CodeHash))
        {
            return CodeCheckOutcome.Valid;
        }

        return Attempts >= MaxAttempts ? CodeCheckOutcome.Expired : CodeCheckOutcome.Invalid;
    }

    public void Reissue(string codeHash, DateTimeOffset now)
    {
        CodeHash = codeHash;
        ExpiresAt = now + Lifetime;
        Attempts = 0;
    }
}
