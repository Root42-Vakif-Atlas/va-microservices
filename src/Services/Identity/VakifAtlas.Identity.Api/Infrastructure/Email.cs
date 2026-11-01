using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MimeKit;

namespace VakifAtlas.Identity.Api.Infrastructure;

public enum EmailKind
{
    VerificationCode,
    PasswordResetCode,

    /// <summary>Kayıtlı adrese tekrar kayıt denendi (numaralandırma koruması: yanıt aynı, e-posta farklı).</summary>
    AccountAlreadyExists,

    /// <summary>VGM Yönetim Paneli'ne personel olarak eklendi (şifre "Şifremi unuttum" ile belirlenir).</summary>
    StaffInvitation,
}

/// <summary>
/// Gönderilecek e-posta (outbox). İsteği yapan transaction'la birlikte yazılır; gönderim
/// arka planda yeniden denenir. Kod, gönderildikten sonra kayıttan silinir.
/// </summary>
public sealed class EmailMessage
{
    public Guid Id { get; init; }

    public required string To { get; init; }

    public EmailKind Kind { get; init; }

    public required string Language { get; init; }

    public string? Code { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? SentAt { get; set; }

    public int Attempts { get; set; }

    public DateTimeOffset? NextAttemptAt { get; set; }
}

public sealed class SmtpOptions
{
    public const string Section = "Smtp";

    /// <summary><c>false</c> → gönderici çalışmaz (testler); iletiler tabloda kalır.</summary>
    public bool Enabled { get; set; } = true;

    public string Host { get; set; } = "localhost";

    public int Port { get; set; } = 1025;

    public bool UseStartTls { get; set; }

    public string? Username { get; set; }

    public string? Password { get; set; }

    public string FromAddress { get; set; } = "no-reply@vakifatlas.gov.tr";

    public string FromName { get; set; } = "Vakıf Atlas";
}

internal static class EmailTemplates
{
    public static (string Subject, string Body) Render(EmailMessage message)
    {
        var english = message.Language == "en";
        return message.Kind switch
        {
            EmailKind.VerificationCode => english
                ? ("Your Vakıf Atlas verification code",
                    $"Your verification code is {message.Code}.\n\nIt is valid for 10 minutes. If you did not create an account, you can ignore this email.")
                : ("Vakıf Atlas doğrulama kodunuz",
                    $"Doğrulama kodunuz: {message.Code}\n\nKod 10 dakika geçerlidir. Hesap oluşturmadıysanız bu e-postayı yok sayabilirsiniz."),
            EmailKind.PasswordResetCode => english
                ? ("Reset your Vakıf Atlas password",
                    $"Your password reset code is {message.Code}.\n\nIt is valid for 10 minutes. If you did not request this, your password has not been changed.")
                : ("Vakıf Atlas şifre sıfırlama",
                    $"Şifre sıfırlama kodunuz: {message.Code}\n\nKod 10 dakika geçerlidir. Bu isteği siz yapmadıysanız şifreniz değişmedi."),
            EmailKind.StaffInvitation => english
                ? ("You have been added to Vakıf Atlas Management",
                    "You have been granted staff access to the Vakıf Atlas Management app. Open the app, choose \"Forgot password\" to set your password with the code sent to this address, then set up two-step verification with an authenticator app.\n\nIf you were not expecting this, contact the VGM IT unit.")
                : ("Vakıf Atlas Yönetim erişiminiz açıldı",
                    "Vakıf Atlas Yönetim uygulamasına personel olarak eklendiniz. Uygulamada \"Şifremi unuttum\" ile bu adrese gelecek kodla şifrenizi belirleyin, ardından bir Authenticator uygulamasıyla iki adımlı doğrulamayı kurun.\n\nBunu beklemiyorsanız VGM Bilgi İşlem birimine başvurun."),
            _ => english
                ? ("Your Vakıf Atlas account",
                    "Someone tried to register with this email address, but an account already exists. You can sign in or reset your password from the app.")
                : ("Vakıf Atlas hesabınız",
                    "Bu e-posta adresiyle yeniden kayıt olunmaya çalışıldı, ancak adrese ait bir hesap zaten var. Uygulamadan giriş yapabilir veya şifrenizi sıfırlayabilirsiniz."),
        };
    }
}

/// <summary>
/// Bekleyen e-postaları SMTP ile gönderir (dev: Mailpit, http://localhost:8025). Birden çok
/// replikada aynı e-posta iki kez gönderilmesin diye satırlar <c>FOR UPDATE SKIP LOCKED</c> ile
/// paylaştırılır (outbox ile aynı desen). Deneme hakkı biten iletinin kodu silinir.
/// </summary>
internal sealed class EmailDispatcher(
    IServiceScopeFactory scopes,
    IOptions<SmtpOptions> options,
    TimeProvider clock,
    ILogger<EmailDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await SendPendingAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Email dispatch failed");
            }
        }
    }

    private async Task SendPendingAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var now = clock.GetUtcNow();
        var pending = await db.Emails
            .FromSql($"""
                SELECT * FROM emails
                WHERE sent_at IS NULL AND attempts < {MaxAttempts}
                  AND (next_attempt_at IS NULL OR next_attempt_at <= {now})
                ORDER BY created_at
                LIMIT 20
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(cancellationToken);
        if (pending.Count == 0)
        {
            return;
        }

        var settings = options.Value;
        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(settings.Host, settings.Port,
            settings.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.None, cancellationToken);
        if (!string.IsNullOrEmpty(settings.Username))
        {
            await smtp.AuthenticateAsync(settings.Username, settings.Password ?? string.Empty, cancellationToken);
        }

        foreach (var message in pending)
        {
            try
            {
                var (subject, body) = EmailTemplates.Render(message);
                using var mime = new MimeMessage();
                mime.From.Add(new MailboxAddress(settings.FromName, settings.FromAddress));
                mime.To.Add(MailboxAddress.Parse(message.To));
                mime.Subject = subject;
                mime.Body = new TextPart("plain") { Text = body };
                await smtp.SendAsync(mime, cancellationToken);
                message.SentAt = clock.GetUtcNow();
                message.Code = null;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                message.Attempts++;
                message.NextAttemptAt = clock.GetUtcNow().AddSeconds(Math.Pow(2, message.Attempts));
                if (message.Attempts >= MaxAttempts)
                {
                    // Gönderilemeyen kod veritabanında süresiz açık metin kalmaz.
                    message.Code = null;
                }

                logger.LogWarning(exception, "Sending {EmailKind} email {EmailId} failed", message.Kind, message.Id);
            }
        }

        await smtp.DisconnectAsync(quit: true, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private const int MaxAttempts = 10;
}
