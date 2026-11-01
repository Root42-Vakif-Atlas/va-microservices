namespace VakifAtlas.Identity.Domain;

public enum PasswordPolicyViolation
{
    Empty,
    TooLong,
}

/// <summary>
/// Yeni şifre kuralları: yalnızca teknik sınırlar. Şifre gücü (uzunluk alt sınırı, yaygın şifre, tekrar,
/// kişisel bilgi, sızıntı denetimi) kullanıcının sorumluluğundadır ve denetlenmez. Üst sınır güvenlik kuralı
/// değil, Argon2 özetleme maliyetini sınırlar (çok uzun girdiyle sunucuyu meşgul etmeyi önler). Girişte uygulanmaz.
/// </summary>
public sealed class PasswordPolicy
{
    public const int MaxLength = 128;

    public PasswordPolicyViolation? Validate(string password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return PasswordPolicyViolation.Empty;
        }

        return password.EnumerateRunes().Count() > MaxLength ? PasswordPolicyViolation.TooLong : null;
    }
}
