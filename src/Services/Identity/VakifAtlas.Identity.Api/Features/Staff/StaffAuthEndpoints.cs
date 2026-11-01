using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using VakifAtlas.Identity.Api.Features.Auth;
using VakifAtlas.Identity.Api.Infrastructure;
using VakifAtlas.Identity.Domain;
using VakifAtlas.ServiceDefaults.Auth;
using VakifAtlas.ServiceDefaults.Http;
using VakifAtlas.SharedKernel;

namespace VakifAtlas.Identity.Api.Features.Staff;

/// <summary>
/// VGM Yönetim Paneli girişi (<c>auth/staff/*</c>): şifre → TOTP (OWASP ASVS V2.8, yönetici
/// hesaplarında MFA zorunlu). Başarılı giriş personel audience'ında oturum açar; vatandaş
/// anahtarı personel uçlarında, personel anahtarı vatandaş uçlarında geçmez. Personel olmayan
/// hesap için yanıt hatalı şifreyle aynıdır (personel listesi açığa çıkmaz).
/// </summary>
public static class StaffAuthEndpoints
{
    public static RouteGroupBuilder MapStaffAuthEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("staff/login", Login).AllowAnonymous();
        group.MapPost("staff/mfa/verify", Verify).AllowAnonymous();
        group.MapPost("staff/mfa/totp/enroll", Enroll).AllowAnonymous();
        group.MapPost("staff/mfa/totp/confirm", Confirm).AllowAnonymous();
        group.MapGet("staff/me", Me).RequireAuthorization(StaffPolicies.Any);
        group.MapPost("staff/recovery-codes", RegenerateRecoveryCodes).RequireAuthorization(StaffPolicies.Any);
        group.MapPost("staff/password/change", ChangePassword).RequireAuthorization(StaffPolicies.Any);
        return group;
    }

    private static async Task<IResult> Login(StaffLoginRequest request, AuthWorkflow workflow, IdentityDbContext db,
        PasswordHasher hasher, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.Email) || string.IsNullOrEmpty(request.Password))
        {
            return ApiError.BadRequest().ToHttpResult();
        }

        var user = await workflow.FindByEmailAsync(Credentials.NormalizeEmail(request.Email), cancellationToken);
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

        if (!StaffAccess.IsAllowed(user))
        {
            return ApiError.Unauthorized(ErrorCodes.InvalidCredentials).ToHttpResult();
        }

        // Sayaç burada sıfırlanmaz: şifreyi bilen biri MFA'yı denerken de kilit sayacı işler.
        await db.MfaChallenges.Where(c => c.UserId == user.Id && (c.ExpiresAt <= now || c.ConsumedAt != null))
            .ExecuteDeleteAsync(cancellationToken);
        var token = SecretTokens.NewRefreshToken();
        var purpose = user.MfaSecret is null ? MfaChallengePurpose.Enroll : MfaChallengePurpose.Verify;
        db.MfaChallenges.Add(new MfaChallenge
        {
            Id = Guid.CreateVersion7(now),
            UserId = user.Id,
            TokenHash = SecretTokens.HashRefreshToken(token),
            Purpose = purpose,
            CreatedAt = now,
            ExpiresAt = now + MfaChallenge.Lifetime,
        });
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(new MfaChallengeResponse(
            purpose == MfaChallengePurpose.Enroll ? "enrollmentRequired" : "required",
            token,
            (int)MfaChallenge.Lifetime.TotalSeconds));
    }

    private static async Task<IResult> Verify(MfaVerifyRequest request, StaffMfaFlow flow, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var hasCode = !string.IsNullOrWhiteSpace(request.Code);
        var hasRecovery = !string.IsNullOrWhiteSpace(request.RecoveryCode);
        if (string.IsNullOrEmpty(request.MfaToken) || hasCode == hasRecovery)
        {
            return ApiError.BadRequest().ToHttpResult();
        }

        var (error, challenge, user) = await flow.BeginAsync(request.MfaToken, MfaChallengePurpose.Verify, cancellationToken);
        if (error is not null)
        {
            return error.ToHttpResult();
        }

        var accepted = hasCode
            ? await flow.AcceptTotpAsync(user!, flow.Secret(user!.MfaSecret!), request.Code!, cancellationToken)
            : await flow.AcceptRecoveryCodeAsync(user!, request.RecoveryCode!, cancellationToken);
        if (!accepted)
        {
            return await flow.RejectAsync(user!, cancellationToken);
        }

        return await flow.CompleteAsync(challenge!, user!, recoveryCodes: null, clock.GetUtcNow(), cancellationToken);
    }

    private static async Task<IResult> Enroll(MfaTokenRequest request, StaffMfaFlow flow, IdentityDbContext db,
        MfaSecretProtector protector, IOptions<MfaOptions> options, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.MfaToken))
        {
            return ApiError.BadRequest().ToHttpResult();
        }

        var (error, challenge, user) = await flow.FindAsync(request.MfaToken, MfaChallengePurpose.Enroll, cancellationToken);
        if (error is not null)
        {
            return error.ToHttpResult();
        }

        // Her çağrı yeni sır üretir (QR yeniden gösterilir); yalnızca onaylanan sır hesaba yazılır.
        var secret = Totp.NewSecret();
        challenge!.PendingSecret = protector.Protect(secret);
        await db.SaveChangesAsync(cancellationToken);
        var issuer = options.Value.Issuer;
        return TypedResults.Ok(new TotpEnrollmentResponse(Base32.Encode(secret), Totp.ProvisioningUri(secret, issuer, user!.Email),
            issuer, user.Email));
    }

    private static async Task<IResult> Confirm(MfaConfirmRequest request, StaffMfaFlow flow, IdentityDbContext db,
        SecretTokens secrets, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.MfaToken) || string.IsNullOrWhiteSpace(request.Code))
        {
            return ApiError.BadRequest().ToHttpResult();
        }

        var (error, challenge, user) = await flow.BeginAsync(request.MfaToken, MfaChallengePurpose.Enroll, cancellationToken);
        if (error is not null)
        {
            return error.ToHttpResult();
        }

        if (challenge!.PendingSecret is null)
        {
            return ApiError.BadRequest(ErrorCodes.MfaNotEnrolled).ToHttpResult();
        }

        var secret = flow.Secret(challenge.PendingSecret);
        var now = clock.GetUtcNow();
        if (Totp.Match(secret, request.Code!, now, lastUsedStep: null) is not { } step)
        {
            return await flow.RejectAsync(user!, cancellationToken);
        }

        user!.MfaSecret = challenge.PendingSecret;
        user.MfaEnrolledAt = now;
        user.LastTotpStep = step;
        await db.RecoveryCodes.Where(c => c.UserId == user.Id).ExecuteDeleteAsync(cancellationToken);
        var codes = RecoveryCodes.Generate(secrets, user.Id, now);
        db.RecoveryCodes.AddRange(codes.Select(c => c.Entity));
        return await flow.CompleteAsync(challenge, user, codes.Select(c => c.Code).ToArray(), now, cancellationToken);
    }

    private static async Task<IResult> Me(ClaimsPrincipal principal, IdentityDbContext db, CancellationToken cancellationToken)
    {
        var userId = principal.RequiredUserId();
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null || !StaffAccess.IsAllowed(user))
        {
            return ApiError.Unauthorized().ToHttpResult();
        }

        return TypedResults.Ok(await StaffMfaFlow.ToStaffUserAsync(db, user, cancellationToken));
    }

    private static async Task<IResult> RegenerateRecoveryCodes(TotpCodeRequest request, ClaimsPrincipal principal,
        IdentityDbContext db, StaffMfaFlow flow, SecretTokens secrets, StaffAuditLog audit, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Code))
        {
            return ApiError.BadRequest().ToHttpResult();
        }

        var userId = principal.RequiredUserId();
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user?.MfaSecret is null || !StaffAccess.IsAllowed(user))
        {
            return ApiError.Unauthorized().ToHttpResult();
        }

        // Kodlar yalnızca Authenticator'a sahip kişiye yeniden verilir (açık bırakılmış oturuma karşı).
        if (!await flow.AcceptTotpAsync(user, flow.Secret(user.MfaSecret), request.Code!, cancellationToken))
        {
            await flow.RecordFailureAsync(user, cancellationToken);
            return ApiError.BadRequest(ErrorCodes.InvalidMfaCode).ToHttpResult();
        }

        var now = clock.GetUtcNow();
        await db.RecoveryCodes.Where(c => c.UserId == user.Id).ExecuteDeleteAsync(cancellationToken);
        var codes = RecoveryCodes.Generate(secrets, user.Id, now);
        db.RecoveryCodes.AddRange(codes.Select(c => c.Entity));
        audit.Record(user.Id, "staff.recovery-codes-regenerated", "staff", user.Id);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(new RecoveryCodesResponse(codes.Select(c => c.Code).ToArray()));
    }

    private static async Task<IResult> ChangePassword(StaffPasswordChangeRequest request, ClaimsPrincipal principal,
        AuthWorkflow workflow, IdentityDbContext db, PasswordHasher hasher, SessionService sessions, TokenService tokens,
        StaffAuditLog audit, CancellationToken cancellationToken)
    {
        if (request.CurrentPassword is null || request.NewPassword is null)
        {
            return ApiError.BadRequest().ToHttpResult();
        }

        var userId = principal.RequiredUserId();
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null || !StaffAccess.IsAllowed(user))
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
        var pair = tokens.StartStaffSession(user);
        audit.Record(user.Id, "staff.password-changed", "staff", user.Id);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(AuthWorkflow.ToTokens(pair));
    }
}
