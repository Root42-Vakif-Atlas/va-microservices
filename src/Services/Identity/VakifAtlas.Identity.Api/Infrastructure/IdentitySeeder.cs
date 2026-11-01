using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using VakifAtlas.Identity.Domain;
using VakifAtlas.Persistence;
using VakifAtlas.ServiceDefaults.Auth;

namespace VakifAtlas.Identity.Api.Infrastructure;

public sealed class IdentitySeedOptions
{
    public const string Section = "Identity:Seed";

    /// <summary><c>seed/legal.json</c> (Flutter mock'tan üretilir; VGM metinleri gelince güncellenir).</summary>
    public string LegalDocumentsPath { get; set; } = "seed/legal.json";

    /// <summary>Demo hesapları (dev/staging). Prod'da kapalı.</summary>
    public bool DemoAccounts { get; set; }

    /// <summary>
    /// VGM personeli demo hesabının şifresi. Personel rolü hasar bildirimlerinin konumunu görür;
    /// şifre kaynak kodda tutulmaz (dev: compose/appsettings.Development; staging: Secret
    /// <c>Identity__Seed__StaffPassword</c>). Boşsa personel hesabı oluşturulmaz.
    /// </summary>
    public string? StaffPassword { get; set; }

    /// <summary>
    /// Demo personel hesaplarının TOTP sırrı (Base32; yalnızca dev/test). Verilirse hesaplar MFA
    /// kurulmuş olarak oluşur ve uçtan uca testler kodu bu sırdan üretir; boşsa personel ilk
    /// girişte Authenticator kurulumu yapar.
    /// </summary>
    public string? StaffTotpSecret { get; set; }
}

/// <summary>Yasal metinler (yoksa/sürüm değiştiyse) ve demo hesaplar; idempotent.</summary>
internal sealed class IdentitySeeder(
    IdentityDbContext db,
    PasswordHasher hasher,
    MfaSecretProtector mfa,
    IOptions<IdentitySeedOptions> options,
    IHostEnvironment environment,
    TimeProvider clock) : IDataSeeder
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        await SeedLegalDocumentsAsync(cancellationToken);
        if (options.Value.DemoAccounts)
        {
            await SeedDemoAccountAsync("usr_demo_001", "vatandas@vakifatlas.dev", "Ayşe Yılmaz", "Atlas2026!", [], cancellationToken);
            if (!string.IsNullOrWhiteSpace(options.Value.StaffPassword))
            {
                foreach (var (id, email, name, role) in StaffAccounts)
                {
                    await SeedDemoAccountAsync(id, email, name, options.Value.StaffPassword, [role], cancellationToken);
                    await SeedStaffMfaAsync(id, cancellationToken);
                }
            }
        }
    }

    /// <summary>Rol başına bir demo personel hesabı (en az yetki ilkesini denemek için).</summary>
    internal static readonly (string Id, string Email, string Name, string Role)[] StaffAccounts =
    [
        ("usr_staff_001", "vgm@vakifatlas.dev", "Akın Bezatoğlu", StaffRoles.Admin),
        ("usr_staff_002", "katalog@vakifatlas.dev", "Katalog Editörü", StaffRoles.Editor),
        ("usr_staff_003", "moderator@vakifatlas.dev", "İçerik Moderatörü", StaffRoles.Moderator),
    ];

    private async Task SeedStaffMfaAsync(string id, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.Value.StaffTotpSecret))
        {
            return;
        }

        var user = await db.Users.SingleAsync(u => u.Id == id, cancellationToken);
        if (user.MfaSecret is null)
        {
            user.MfaSecret = mfa.Protect(Base32.Decode(options.Value.StaffTotpSecret));
            user.MfaEnrolledAt = clock.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task SeedLegalDocumentsAsync(CancellationToken cancellationToken)
    {
        var path = new[] { environment.ContentRootPath, AppContext.BaseDirectory }
            .Select(root => Path.Combine(root, options.Value.LegalDocumentsPath))
            .FirstOrDefault(File.Exists);
        if (path is null)
        {
            return;
        }

        await using var stream = File.OpenRead(path);
        var seed = await JsonSerializer.DeserializeAsync<LegalSeed>(stream, Json, cancellationToken);
        foreach (var document in seed?.Documents ?? [])
        {
            var existing = await db.LegalDocuments.FindAsync([document.Type, document.Language], cancellationToken);
            if (existing is null)
            {
                db.LegalDocuments.Add(new LegalDocument
                {
                    Type = document.Type,
                    Language = document.Language,
                    Version = document.Version,
                    Title = document.Title,
                    Body = document.Body,
                    EffectiveDate = document.EffectiveDate,
                });
            }
            else if (existing.Version != document.Version)
            {
                existing.Version = document.Version;
                existing.Title = document.Title;
                existing.Body = document.Body;
                existing.EffectiveDate = document.EffectiveDate;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Demo şifresi yeni politikadan kısadır: politika yalnızca yeni şifrede uygulanır (eski hesap senaryosu).</summary>
    private async Task SeedDemoAccountAsync(string id, string email, string displayName, string password,
        List<string> roles, CancellationToken cancellationToken)
    {
        if (await db.Users.AnyAsync(u => u.Id == id || u.Email == email, cancellationToken))
        {
            return;
        }

        var now = clock.GetUtcNow();
        db.Users.Add(new User
        {
            Id = id,
            Email = email,
            DisplayName = displayName,
            PasswordHash = await hasher.HashAsync(password, cancellationToken),
            EmailVerified = true,
            CreatedAt = now,
            TermsVersion = "seed",
            PrivacyNoticeVersion = "seed",
            ConsentedAt = now,
            Roles = roles,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    private sealed record LegalSeed(List<LegalSeedDocument> Documents);

    private sealed record LegalSeedDocument(string Type, string Language, string Version, string Title, string Body,
        DateOnly EffectiveDate);
}
