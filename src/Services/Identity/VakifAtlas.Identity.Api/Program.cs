using Microsoft.AspNetCore.Authentication.JwtBearer;
using VakifAtlas.Contracts;
using VakifAtlas.Identity.Api.Features.Auth;
using VakifAtlas.Identity.Api.Features.Discovery;
using VakifAtlas.Identity.Api.Features.Legal;
using VakifAtlas.Identity.Api.Features.Staff;
using VakifAtlas.Identity.Api.Infrastructure;
using VakifAtlas.Identity.Domain;
using VakifAtlas.Messaging;
using VakifAtlas.Persistence;
using VakifAtlas.ServiceDefaults;
using VakifAtlas.ServiceDefaults.Admin;
using VakifAtlas.ServiceDefaults.Auth;
using VakifAtlas.ServiceDefaults.Redis;
using VakifAtlas.ServiceDefaults.Versioning;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults("identity");
builder.AddRedis();
builder.AddJwtAuthentication();
builder.AddMessaging<IdentityDbContext>("identity")
    .Subscribe<StaffActionRecorded, StaffActionRecordedHandler>();
builder.AddPostgres<IdentityDbContext>();

builder.Services.Configure<IdentitySecretsOptions>(builder.Configuration.GetSection(IdentitySecretsOptions.Section));
builder.Services.Configure<SigningOptions>(builder.Configuration.GetSection(SigningOptions.Section));
builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection(SmtpOptions.Section));
builder.Services.Configure<IdentitySeedOptions>(builder.Configuration.GetSection(IdentitySeedOptions.Section));
builder.Services.Configure<MfaOptions>(builder.Configuration.GetSection(MfaOptions.Section));

builder.Services.AddSingleton<PasswordHasher>();
builder.Services.AddSingleton<SecretTokens>();
builder.Services.AddSingleton<PasswordPolicy>();
builder.Services.AddSingleton<SigningKeyStore>();
builder.Services.AddHostedService<SigningKeyInitializer>();
builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<SessionService>();
builder.Services.AddScoped<AuthWorkflow>();
builder.Services.AddSingleton<MfaSecretProtector>();
builder.Services.AddScoped<StaffAuditLog>();
builder.Services.AddScoped<StaffMfaFlow>();
builder.Services.AddScoped<StaffAdminService>();
builder.Services.AddDataSeeder<IdentitySeeder>();
if (builder.Configuration.GetValue($"{SmtpOptions.Section}:Enabled", defaultValue: true))
{
    builder.Services.AddHostedService<EmailDispatcher>();
}

// Identity kendi anahtarlarını bellekten doğrular (kendine HTTP ile keşif yapmaz).
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .PostConfigure<SigningKeyStore>((options, keys) =>
    {
        options.Authority = null;
        options.ConfigurationManager = null;
        options.TokenValidationParameters.IssuerSigningKeyResolver = (_, _, _, _) => keys.Keys;
    });

var app = builder.Build();

app.UseServiceDefaults();
app.UseAuthentication();
app.UseAuthorization();

app.MapVersionedGroup("auth").MapAuthEndpoints().MapStaffAuthEndpoints().WithTags("Auth");
app.MapAdminGroup("auth/admin", StaffPolicies.Admin).MapStaffAdminEndpoints().WithTags("Staff administration");
app.MapVersionedGroup("legal").MapLegalEndpoints().WithTags("Legal");
app.MapDiscoveryEndpoints();
app.MapDefaultEndpoints();

app.Run();

/// <summary>WebApplicationFactory testleri için.</summary>
public partial class Program
{
    protected Program()
    {
    }
}
