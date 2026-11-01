using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using VakifAtlas.ServiceDefaults.Http;
using VakifAtlas.SharedKernel;

namespace VakifAtlas.ServiceDefaults.Auth;

/// <summary>JWT settings from the <c>Auth</c> section.</summary>
public sealed class AuthOptions
{
    /// <summary>Configuration section name.</summary>
    public const string Section = "Auth";

    /// <summary>Identity service URL for OIDC discovery.</summary>
    public string Authority { get; set; } = "http://identity:8080";

    /// <summary>Expected token issuer.</summary>
    public string Issuer { get; set; } = "https://api.vakifatlas.gov.tr";

    /// <summary>Audience of citizen tokens.</summary>
    public string Audience { get; set; } = "vakif-atlas-api";

    /// <summary>Audience of staff tokens.</summary>
    public string AdminAudience { get; set; } = "vakif-atlas-admin";

    /// <summary>Requires HTTPS for OIDC metadata.</summary>
    public bool RequireHttpsMetadata { get; set; }

    /// <summary>Static JWKS JSON that replaces OIDC discovery.</summary>
    public string? Jwks { get; set; }
}

/// <summary>JWT claim names.</summary>
public static class VakifAtlasClaims
{
    /// <summary>User ID claim.</summary>
    public const string Subject = "sub";

    /// <summary>Display name claim.</summary>
    public const string Name = "name";

    /// <summary>Session ID claim.</summary>
    public const string SessionId = "sid";

    /// <summary>Staff roles claim.</summary>
    public const string Roles = "roles";

    /// <summary>Audience claim.</summary>
    public const string Audience = "aud";

    /// <summary>Authentication methods claim.</summary>
    public const string AuthenticationMethods = "amr";

    /// <summary><c>amr</c> value for a completed OTP step.</summary>
    public const string OneTimePasswordMethod = "otp";
}

/// <summary>Staff role names.</summary>
public static class StaffRoles
{
    /// <summary>Full staff access.</summary>
    public const string Admin = "vgm_admin";

    /// <summary>Catalog access.</summary>
    public const string Editor = "editor";

    /// <summary>User content moderation.</summary>
    public const string Moderator = "moderator";

    /// <summary>All staff roles.</summary>
    public static readonly IReadOnlyList<string> All = [Admin, Editor, Moderator];

    /// <summary>Checks whether <paramref name="role"/> is a staff role.</summary>
    /// <param name="role">Role name to check.</param>
    public static bool IsStaffRole(string role) => All.Contains(role, StringComparer.Ordinal);
}

/// <summary>Authorization policies for staff endpoints.</summary>
public static class StaffPolicies
{
    /// <summary>Any staff role.</summary>
    public const string Any = "staff";

    /// <summary>Catalog management.</summary>
    public const string Catalog = "staff:catalog";

    /// <summary>Memory moderation.</summary>
    public const string Memories = "staff:memories";

    /// <summary>Civil Protection reports.</summary>
    public const string CivilProtection = "staff:civil-protection";

    /// <summary>Feedback management.</summary>
    public const string Feedback = "staff:feedback";

    /// <summary>Staff management.</summary>
    public const string Admin = "staff:admin";

    /// <summary>Audit log access.</summary>
    public const string Audit = "staff:audit";

    internal static readonly IReadOnlyDictionary<string, string[]> Roles = new Dictionary<string, string[]>
    {
        [Any] = [.. StaffRoles.All],
        [Catalog] = [StaffRoles.Admin, StaffRoles.Editor],
        [Memories] = [StaffRoles.Admin, StaffRoles.Moderator],
        [CivilProtection] = [StaffRoles.Admin, StaffRoles.Moderator],
        [Feedback] = [StaffRoles.Admin, StaffRoles.Moderator],
        [Admin] = [StaffRoles.Admin],
        [Audit] = [StaffRoles.Admin],
    };
}

/// <summary>JWT authentication and claim helpers.</summary>
public static class AuthenticationExtensions
{
    /// <summary>Adds ES256 JWT validation, session revocation checks and staff policies.</summary>
    /// <param name="builder">The application builder.</param>
    public static WebApplicationBuilder AddJwtAuthentication(this WebApplicationBuilder builder)
    {
        AuthOptions options = builder.Configuration.GetSection(AuthOptions.Section).Get<AuthOptions>() ?? new AuthOptions();
        builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.Section));

        builder.Services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(jwt =>
            {
                if (string.IsNullOrWhiteSpace(options.Jwks))
                {
                    jwt.Authority = options.Authority;
                }
                else
                {
                    jwt.TokenValidationParameters.IssuerSigningKeys = new JsonWebKeySet(options.Jwks).GetSigningKeys();
                }

                jwt.RequireHttpsMetadata = options.RequireHttpsMetadata;
                jwt.MapInboundClaims = false;
                jwt.TokenValidationParameters.ValidIssuer = options.Issuer;
                jwt.TokenValidationParameters.ValidAudiences = [options.Audience, options.AdminAudience];
                jwt.TokenValidationParameters.ValidAlgorithms = ["ES256"];
                jwt.TokenValidationParameters.NameClaimType = VakifAtlasClaims.Name;
                jwt.TokenValidationParameters.RoleClaimType = VakifAtlasClaims.Roles;
                jwt.TokenValidationParameters.ClockSkew = TimeSpan.FromSeconds(30);
                jwt.Events = new JwtBearerEvents
                {
                    OnTokenValidated = async context =>
                    {
                        ISessionRevocationStore? store = context.HttpContext.RequestServices.GetService<ISessionRevocationStore>();
                        var sessionId = context.Principal?.FindFirstValue(VakifAtlasClaims.SessionId);
                        if (store is not null && sessionId is not null &&
                            await store.IsRevokedAsync(sessionId, context.HttpContext.RequestAborted))
                        {
                            context.Fail("session_revoked");
                        }
                    },
                    OnChallenge = async context =>
                    {
                        context.HandleResponse();
                        await ApiError.Unauthorized().ToHttpResult().ExecuteAsync(context.HttpContext);
                    },
                    OnForbidden = context =>
                        ApiError.NotFound().ToHttpResult().ExecuteAsync(context.HttpContext),
                };
            });

        // Default policy accepts citizen tokens only; staff policies need the admin audience, OTP and a role.
        AuthorizationBuilder authorization = builder.Services.AddAuthorizationBuilder()
            .SetDefaultPolicy(new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .RequireClaim(VakifAtlasClaims.Audience, options.Audience)
                .Build());
        foreach ((string name, string[] roles) in StaffPolicies.Roles)
        {
            authorization.AddPolicy(name, policy => policy
                .RequireAuthenticatedUser()
                .RequireClaim(VakifAtlasClaims.Audience, options.AdminAudience)
                .RequireClaim(VakifAtlasClaims.AuthenticationMethods, VakifAtlasClaims.OneTimePasswordMethod)
                .RequireRole(roles));
        }

        return builder;
    }

    /// <summary>Gets the user ID, or <c>null</c> for guests.</summary>
    /// <param name="principal">The current user.</param>
    public static string? UserId(this ClaimsPrincipal principal) =>
        principal.Identity?.IsAuthenticated == true ? principal.FindFirstValue(VakifAtlasClaims.Subject) : null;

    /// <summary>Gets the user ID; throws if the endpoint lacks authorization.</summary>
    /// <param name="principal">The current user.</param>
    public static string RequiredUserId(this ClaimsPrincipal principal) =>
        principal.UserId() ?? throw new InvalidOperationException("Endpoint requires authorization.");

    /// <summary>Gets the display name, or an empty string.</summary>
    /// <param name="principal">The current user.</param>
    public static string DisplayName(this ClaimsPrincipal principal) =>
        principal.FindFirstValue(VakifAtlasClaims.Name) ?? string.Empty;

    /// <summary>Gets the session ID.</summary>
    /// <param name="principal">The current user.</param>
    public static string? SessionId(this ClaimsPrincipal principal) =>
        principal.FindFirstValue(VakifAtlasClaims.SessionId);

    /// <summary>Gets the citizen user ID, or <c>null</c> for guests and staff.</summary>
    /// <param name="context">The current request.</param>
    public static string? OptionalUserId(this HttpContext context)
    {
        var audience = context.RequestServices.GetService<Microsoft.Extensions.Options.IOptions<AuthOptions>>()?.Value.Audience
            ?? new AuthOptions().Audience;
        return context.User.HasClaim(VakifAtlasClaims.Audience, audience) ? context.User.UserId() : null;
    }

    /// <summary>Checks whether the token is a staff token.</summary>
    /// <param name="principal">The current user.</param>
    /// <param name="options">Auth settings with the staff audience.</param>
    public static bool IsStaffSession(this ClaimsPrincipal principal, AuthOptions options) =>
        principal.HasClaim(VakifAtlasClaims.Audience, options.AdminAudience);
}

/// <summary>Stores revoked session IDs.</summary>
public interface ISessionRevocationStore
{
    /// <summary>Revokes the given sessions.</summary>
    /// <param name="sessionIds">Session IDs to revoke.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RevokeAsync(IEnumerable<string> sessionIds, CancellationToken cancellationToken);

    /// <summary>Checks whether a session is revoked.</summary>
    /// <param name="sessionId">Session ID to check.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<bool> IsRevokedAsync(string sessionId, CancellationToken cancellationToken);
}
