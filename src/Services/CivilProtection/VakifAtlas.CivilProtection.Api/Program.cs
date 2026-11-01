using VakifAtlas.CatalogClient;
using VakifAtlas.CivilProtection.Api.Features;
using VakifAtlas.CivilProtection.Api.Infrastructure;
using VakifAtlas.Contracts;
using VakifAtlas.Media;
using VakifAtlas.Messaging;
using VakifAtlas.Persistence;
using VakifAtlas.ServiceDefaults;
using VakifAtlas.ServiceDefaults.Admin;
using VakifAtlas.ServiceDefaults.Auth;
using VakifAtlas.ServiceDefaults.Redis;
using VakifAtlas.ServiceDefaults.Versioning;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults("civil-protection");
builder.AddRedis();
builder.AddJwtAuthentication();
builder.AddMessaging<CivilProtectionDbContext>("civil-protection")
    .Subscribe<AccountDeleted, AccountDeletedHandler>();
builder.AddPostgres<CivilProtectionDbContext>();
builder.AddCatalogClient();
builder.AddObjectStorage();

builder.Services.Configure<CivilProtectionOptions>(builder.Configuration.GetSection(CivilProtectionOptions.Section));
builder.Services.AddPhotoUploads<CivilProtectionDbContext>();
builder.AddReportEvidence();
builder.Services.AddSingleton<ReportMaintenance>();
builder.Services.AddHostedService(services => services.GetRequiredService<ReportMaintenance>());
if (builder.Configuration.GetValue("CivilProtection:SeedDemoReports", defaultValue: false))
{
    builder.Services.AddDataSeeder<DemoReportSeeder>();
}

var app = builder.Build();

app.UseServiceDefaults();
app.UseAuthentication();
app.UseAuthorization();

app.MapVersionedGroup("civil-protection").MapReportEndpoints().WithTags("Civil protection");
app.MapAdminGroup("civil-protection/admin", StaffPolicies.CivilProtection).MapStaffEndpoints()
    .WithTags("Civil protection (VGM staff)");
app.MapDefaultEndpoints();

app.Run();

/// <summary>WebApplicationFactory testleri için.</summary>
public partial class Program
{
    protected Program()
    {
    }
}
