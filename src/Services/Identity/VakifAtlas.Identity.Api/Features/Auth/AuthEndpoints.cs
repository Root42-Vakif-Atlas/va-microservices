using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VakifAtlas.Contracts;
using VakifAtlas.Identity.Api.Infrastructure;
using VakifAtlas.Identity.Domain;
using VakifAtlas.Messaging;
using VakifAtlas.ServiceDefaults.Auth;
using VakifAtlas.ServiceDefaults.Http;
using VakifAtlas.SharedKernel;

namespace VakifAtlas.Identity.Api.Features.Auth;

/// <summary>
/// <c>auth/*</c> (api-contracts.md → Kimlik Doğrulama). Numaralandırma koruması: kayıt,
/// şifremi unuttum ve kod yeniden gönderimi hesabın varlığını açığa vurmaz (ADR 13).
/// </summary>
public static class AuthEndpoints
{
    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("register", Register).AllowAnonymous();
        group.MapPost("verify-email", VerifyEmail).AllowAnonymous();
        group.MapPost("resend-verification", ResendVerification).AllowAnonymous();
        group.MapPost("login", Login).AllowAnonymous();
        group.MapPost("refresh", Refresh).AllowAnonymous();
        group.MapPost("logout", Logout).AllowAnonymous();
        group.MapGet("me", Me).RequireAuthorization();
        group.MapPost("password/forgot", ForgotPassword).AllowAnonymous();
        group.MapPost("password/reset", ResetPassword).AllowAnonymous();
        group.MapPost("password/change", ChangePassword).RequireAuthorization();
        group.MapDelete("account", DeleteAccount).RequireAuthorization();
        return group;
    }

    private static async Task<IResult> Register(RegisterRequest request, HttpRequest http, AuthWorkflow workflow,
        IdentityDbContext db, PasswordHasher hasher, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (request.DisplayName is null || request.Email is null || request.Password is null ||
            !Credentials.IsValidDisplayName(request.DisplayName) || !Credentials.IsValidEmail(request.Email))
        {
            return ApiError.BadRequest().ToHttpResult();
        }

        if (request.Consents is not { AcceptedTerms: true, AcknowledgedPrivacyNotice: true })
        {
            return ApiError.BadRequest(ErrorCodes.ConsentRequired).ToHttpResult();
        }

        var email = Credentials.NormalizeEmail(request.Email);
        var displayName = Credentials.NormalizeDisplayName(request.DisplayName);
        if (!await workflow.IsAcceptablePasswordAsync(request.Password, email, displayName, cancellationToken))
        {
            return ApiError.BadRequest(ErrorCodes.WeakPassword).ToHttpResult();
        }

        var language = RequestLanguage.Resolve(http);
        var existing = await workflow.FindByEmailAsync(email, cancellationToken);
        if (existing is null)
        {
            var now = clock.GetUtcNow();
            var versions = await db.LegalDocuments.AsNoTracking().Where(d => d.Language == RequestLanguage.Turkish)
                .ToDictionaryAsync(d => d.Type, d => d.Version, cancellationToken);
            db.Users.Add(new User
            {
                Id = Ids.New("usr", 9),
                Email = email,
                DisplayName = displayName,
                PasswordHash = await hasher.HashAsync(request.Password, cancellationToken),
                CreatedAt = now,
                TermsVersion = versions.GetValueOrDefault(LegalDocument.TermsOfUse, "unknown"),
                PrivacyNoticeVersion = versions.GetValueOrDefault(LegalDocument.PrivacyNotice, "unknown"),
                ConsentedAt = now,
            });
            await workflow.TryStartCooldownAsync(email, CodePurpose.VerifyEmail, cancellationToken);
            await workflow.IssueCodeAsync(email, CodePurpose.VerifyEmail, language, cancellationToken);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // Eşzamanlı aynı kayıt: yanıt yine aynıdır.
            }

            return TypedResults.Accepted((string?)null, AuthWorkflow.Challenge);
        }

        // Yeni hesap yolundaki özet maliyeti burada da ödenir (zamanlama eşitliği).
        await hasher.VerifyDummyAsync(request.Password, cancellationToken);
        // (Doğrulanmamış hesapta yeni kod gönderilirse şifre aşağıda yeniden özetlenir.)
        if ((await workflow.TryStartCooldownAsync(email, CodePurpose.VerifyEmail, cancellationToken)).Allowed)
        {
            if (existing.EmailVerified)
            {
                workflow.QueueEmail(email, EmailKind.AccountAlreadyExists, language);
            }
            else
            {
                // Doğrulanmamış hesap: yeni kodu tetikleyen son kaydın bilgileri geçerlidir (eski
                // kayıttaki ad/şifre kalmaz). Şifre ayrıca doğrulamada kodu girenden alınır.
                var now = clock.GetUtcNow();
                var versions = await db.LegalDocuments.AsNoTracking().Where(d => d.Language == RequestLanguage.Turkish)
                    .ToDictionaryAsync(d => d.Type, d => d.Version, cancellationToken);
                existing.ReplacePendingRegistration(
                    displayName,
                    await hasher.HashAsync(request.Password, cancellationToken),
                    versions.GetValueOrDefault(LegalDocument.TermsOfUse, "unknown"),
                    versions.GetValueOrDefault(LegalDocument.PrivacyNotice, "unknown"),
                    now);
                await workflow.IssueCodeAsync(email, CodePurpose.VerifyEmail, language, cancellationToken);
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        return TypedResults.Accepted((string?)null, AuthWorkflow.Challenge);
    }

    private static async Task<IResult> VerifyEmail(VerifyEmailRequest request, AuthWorkflow workflow, IdentityDbContext db,
        PasswordHasher hasher, TokenService tokens, CancellationToken cancellationToken)
    {
        if (request.Email is null || request.Code is null || string.IsNullOrEmpty(request.Password))
        {
            return ApiError.BadRequest().ToHttpResult();
        }

        var email = Credentials.NormalizeEmail(request.Email);
        var user = await workflow.FindByEmailAsync(email, cancellationToken);
        if (user is null || user.EmailVerified)
        {
            return ApiError.BadRequest(ErrorCodes.CodeExpired).ToHttpResult();
        }

        var (outcome, code) = await workflow.CheckCodeAsync(email, CodePurpose.VerifyEmail, request.Code, cancellationToken);
        if (outcome != CodeCheckOutcome.Valid)
        {
            await db.SaveChangesAsync(cancellationToken);
            return ApiError.BadRequest(outcome == CodeCheckOutcome.Invalid ? ErrorCodes.InvalidCode : ErrorCodes.CodeExpired)
                .ToHttpResult();
        }

        // Kod doğru ama şifre zayıfsa kod harcanmaz; kullanıcı yeniden dener.
        if (!await workflow.IsAcceptablePasswordAsync(request.Password, email, user.DisplayName, cancellationToken))
        {
            return ApiError.BadRequest(ErrorCodes.WeakPassword).ToHttpResult();
        }

        db.Codes.Remove(code!);
        user.PasswordHash = await hasher.HashAsync(request.Password, cancellationToken);
        user.EmailVerified = true;
        user.ClearLock();
        var pair = tokens.StartSession(user);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(AuthWorkflow.ToSession(pair, user));
    }

    private static async Task<IResult> ResendVerification(EmailRequest request, HttpRequest http, AuthWorkflow workflow,
        IdentityDbContext db, CancellationToken cancellationToken)
    {
        if (request.Email is null || !Credentials.IsValidEmail(request.Email))
        {
            return ApiError.BadRequest().ToHttpResult();
        }

        var email = Credentials.NormalizeEmail(request.Email);
        var cooldown = await workflow.TryStartCooldownAsync(email, CodePurpose.VerifyEmail, cancellationToken);
        if (!cooldown.Allowed)
        {
            return ApiError.TooManyRequests(cooldown.RetryAfterSeconds).ToHttpResult();
        }

        var user = await workflow.FindByEmailAsync(email, cancellationToken);
        if (user is { EmailVerified: false })
        {
            await workflow.IssueCodeAsync(email, CodePurpose.VerifyEmail, RequestLanguage.Resolve(http), cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        return TypedResults.Accepted((string?)null, AuthWorkflow.Challenge);
    }

    private static async Task<IResult> Login(LoginRequest request, HttpRequest http, AuthWorkflow workflow,
        IdentityDbContext db, PasswordHasher hasher, TokenService tokens, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.Email) || string.IsNullOrEmpty(request.Password))
        {
            return ApiError.BadRequest().ToHttpResult();
        }

        var email = Credentials.NormalizeEmail(request.Email);
        var user = await workflow.FindByEmailAsync(email, cancellationToken);
        if (user is null)
        {
            await hasher.VerifyDummyAsync(request.Password, cancellationToken);
            return ApiError.Unauthorized(ErrorCodes.InvalidCredentials).ToHttpResult();
        }

        var now = clock.GetUtcNow();
        if (user.LockRemaining(now) is { } remaining)
        {
            return ApiError.Locked((int)Math.Ceiling(remaining.TotalSeconds)).ToHttpResult();
        }

        if (!await hasher.VerifyAsync(request.Password, user.PasswordHash, cancellationToken))
        {
            await workflow.RecordFailedLoginAsync(user.Id, cancellationToken);
            return ApiError.Unauthorized(ErrorCodes.InvalidCredentials).ToHttpResult();
        }

        user.ClearLock();
        if (!user.EmailVerified)
        {
            // İstemci kendiliğinden kod istemez; bekleme süresi uygunsa yeni kod gönderilir.
            var cooldown = await workflow.TryStartCooldownAsync(email, CodePurpose.VerifyEmail, cancellationToken);
            if (cooldown.Allowed)
            {
                await workflow.IssueCodeAsync(email, CodePurpose.VerifyEmail, RequestLanguage.Resolve(http), cancellationToken);
            }

            await db.SaveChangesAsync(cancellationToken);
            // Doğrulama ekranındaki "Kodu tekrar gönder" sayacı sunucudaki bekleme süresiyle
            // başlar; aksi halde buton hemen etkin olur ve ilk basış 429 alırdı.
            var resendAfter = cooldown.Allowed ? (int)OneTimeCode.ResendCooldown.TotalSeconds : cooldown.RetryAfterSeconds;
            return ApiError.Forbidden(ErrorCodes.EmailNotVerified, resendAfter).ToHttpResult();
        }

        var pair = tokens.StartSession(user);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(AuthWorkflow.ToSession(pair, user));
    }

    private static async Task<IResult> Refresh(RefreshRequest request, SessionService sessions, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.RefreshToken) || request.RefreshToken.Length > 200)
        {
            return ApiError.Unauthorized(ErrorCodes.InvalidRefreshToken).ToHttpResult();
        }

        var (outcome, pair) = await sessions.RotateAsync(request.RefreshToken, cancellationToken);
        return outcome == RefreshOutcome.Rotated
            ? TypedResults.Ok(AuthWorkflow.ToTokens(pair!))
            : ApiError.Unauthorized(ErrorCodes.InvalidRefreshToken).ToHttpResult();
    }

    private static async Task<IResult> Logout(RefreshRequest request, SessionService sessions, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(request.RefreshToken) && request.RefreshToken.Length <= 200)
        {
            await sessions.LogoutAsync(request.RefreshToken, cancellationToken);
        }

        return TypedResults.NoContent();
    }

    private static async Task<IResult> Me(ClaimsPrincipal principal, IdentityDbContext db, CancellationToken cancellationToken)
    {
        var userId = principal.RequiredUserId();
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        return user is null ? ApiError.Unauthorized().ToHttpResult() : TypedResults.Ok(AuthWorkflow.ToResponse(user));
    }

    private static async Task<IResult> ForgotPassword(EmailRequest request, HttpRequest http, AuthWorkflow workflow,
        IdentityDbContext db, CancellationToken cancellationToken)
    {
        if (request.Email is null || !Credentials.IsValidEmail(request.Email))
        {
            return ApiError.BadRequest().ToHttpResult();
        }

        var email = Credentials.NormalizeEmail(request.Email);
        var cooldown = await workflow.TryStartCooldownAsync(email, CodePurpose.ResetPassword, cancellationToken);
        if (!cooldown.Allowed)
        {
            return ApiError.TooManyRequests(cooldown.RetryAfterSeconds).ToHttpResult();
        }

        var user = await workflow.FindByEmailAsync(email, cancellationToken);
        if (user is { EmailVerified: true })
        {
            await workflow.IssueCodeAsync(email, CodePurpose.ResetPassword, RequestLanguage.Resolve(http), cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        return TypedResults.Accepted((string?)null, AuthWorkflow.Challenge);
    }

    private static async Task<IResult> ResetPassword(ResetPasswordRequest request, AuthWorkflow workflow, IdentityDbContext db,
        PasswordHasher hasher, SessionService sessions, CancellationToken cancellationToken)
    {
        if (request.Email is null || request.Code is null || request.NewPassword is null)
        {
            return ApiError.BadRequest().ToHttpResult();
        }

        var email = Credentials.NormalizeEmail(request.Email);
        var user = await workflow.FindByEmailAsync(email, cancellationToken);
        if (user is null)
        {
            return ApiError.BadRequest(ErrorCodes.CodeExpired).ToHttpResult();
        }

        var (outcome, code) = await workflow.CheckCodeAsync(email, CodePurpose.ResetPassword, request.Code, cancellationToken);
        if (outcome != CodeCheckOutcome.Valid)
        {
            await db.SaveChangesAsync(cancellationToken);
            return ApiError.BadRequest(outcome == CodeCheckOutcome.Invalid ? ErrorCodes.InvalidCode : ErrorCodes.CodeExpired)
                .ToHttpResult();
        }

        // Kod doğru ama şifre zayıfsa kod harcanmaz; kullanıcı yeniden dener.
        if (!await workflow.IsAcceptablePasswordAsync(request.NewPassword, email, user.DisplayName, cancellationToken))
        {
            return ApiError.BadRequest(ErrorCodes.WeakPassword).ToHttpResult();
        }

        db.Codes.Remove(code!);
        user.PasswordHash = await hasher.HashAsync(request.NewPassword, cancellationToken);
        user.ClearLock();
        await db.SaveChangesAsync(cancellationToken);
        await sessions.RevokeAllAsync(user.Id, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> ChangePassword(ChangePasswordRequest request, ClaimsPrincipal principal,
        AuthWorkflow workflow, IdentityDbContext db, PasswordHasher hasher, SessionService sessions, TokenService tokens,
        CancellationToken cancellationToken)
    {
        if (request.CurrentPassword is null || request.NewPassword is null)
        {
            return ApiError.BadRequest().ToHttpResult();
        }

        var userId = principal.RequiredUserId();
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return ApiError.Unauthorized().ToHttpResult();
        }

        // 400 (401 değil): aksi halde istemci oturum yenilemeyi tetikler.
        if (!await hasher.VerifyAsync(request.CurrentPassword, user.PasswordHash, cancellationToken))
        {
            return ApiError.BadRequest(ErrorCodes.InvalidCurrentPassword).ToHttpResult();
        }

        if (request.NewPassword == request.CurrentPassword ||
            !await workflow.IsAcceptablePasswordAsync(request.NewPassword, user.Email, user.DisplayName, cancellationToken))
        {
            return ApiError.BadRequest(ErrorCodes.WeakPassword).ToHttpResult();
        }

        user.PasswordHash = await hasher.HashAsync(request.NewPassword, cancellationToken);
        await sessions.RevokeAllAsync(user.Id, cancellationToken);
        var pair = tokens.StartSession(user);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(AuthWorkflow.ToTokens(pair));
    }

    private static async Task<IResult> DeleteAccount([FromBody] DeleteAccountRequest request, ClaimsPrincipal principal,
        IdentityDbContext db, PasswordHasher hasher, SessionService sessions, IEventPublisher events, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var userId = principal.RequiredUserId();
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return ApiError.Unauthorized().ToHttpResult();
        }

        if (request.Password is null || !await hasher.VerifyAsync(request.Password, user.PasswordHash, cancellationToken))
        {
            return ApiError.BadRequest(ErrorCodes.InvalidCurrentPassword).ToHttpResult();
        }

        await sessions.RevokeAllAsync(user.Id, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.RefreshTokens.Where(t => t.UserId == user.Id).ExecuteDeleteAsync(cancellationToken);
        await db.Codes.Where(c => c.Email == user.Email).ExecuteDeleteAsync(cancellationToken);
        await db.Emails.Where(e => e.To == user.Email).ExecuteDeleteAsync(cancellationToken);
        db.Users.Remove(user);

        // Diğer servisler kişisel veriyi siler/anonimleştirir (KVKK; App Store 5.1.1(v)).
        events.Publish(new AccountDeleted(Guid.NewGuid(), clock.GetUtcNow(), user.Id));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TypedResults.NoContent();
    }
}
