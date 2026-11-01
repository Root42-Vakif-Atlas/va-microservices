using Microsoft.Extensions.DependencyInjection.Extensions;
using VakifAtlas.Catalog.Api.Features.Admin;
using VakifAtlas.Catalog.Api.Features.Complexes;
using VakifAtlas.Catalog.Api.Features.Map;
using VakifAtlas.Catalog.Api.Features.Works;
using VakifAtlas.Catalog.Api.Infrastructure;
using VakifAtlas.Catalog.Api.Infrastructure.Import;
using VakifAtlas.Catalog.Api.Infrastructure.Mirroring;
using VakifAtlas.Catalog.Api.Infrastructure.Persistence;
using VakifAtlas.Catalog.Api.Infrastructure.Reading;
using VakifAtlas.Media;
using VakifAtlas.Messaging;
using VakifAtlas.Persistence;
using VakifAtlas.ServiceDefaults;
using VakifAtlas.ServiceDefaults.Admin;
using VakifAtlas.ServiceDefaults.Auth;
using VakifAtlas.ServiceDefaults.Redis;
using VakifAtlas.ServiceDefaults.Versioning;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults("catalog");
builder.AddRedis();
builder.AddJwtAuthentication();
builder.AddPostgres<CatalogDbContext>();
builder.AddMessaging<CatalogDbContext>("catalog");
builder.AddObjectStorage();

builder.Services.AddStackExchangeRedisOutputCache(options =>
{
    options.Configuration = builder.Configuration.GetConnectionString("Redis");
    // tests share one Redis: a prefix per factory
    options.InstanceName = builder.Configuration["Catalog:CacheInstanceName"] ?? "catalog:";
});
builder.Services.AddOutputCache(CatalogCaching.AddPolicies);

builder.Services.Configure<CatalogSeedOptions>(builder.Configuration.GetSection(CatalogSeedOptions.Section));
builder.Services.Configure<ImageMirrorOptions>(builder.Configuration.GetSection(ImageMirrorOptions.Section));
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.TryAddSingleton<PhotoProcessor>();
builder.Services.AddPhotoUploads<CatalogDbContext>();
builder.Services.AddSingleton<MediaUrls>();
builder.Services.AddSingleton<ImageMirrorSignal>();
builder.Services.AddScoped<CatalogReadStore>();
builder.Services.AddScoped<CatalogImporter>();
builder.Services.AddScoped<ImportImageStore>();
builder.Services.AddScoped<CatalogFileImport>();
builder.Services.AddScoped<CatalogAdminService>();
builder.Services.AddDataSeeder<CatalogSeeder>();
builder.Services.AddHostedService<ImageMirrorService>();
builder.Services.AddHttpClient(ImageMirrorService.HttpClientName, http =>
    {
        http.Timeout = TimeSpan.FromSeconds(30);
        http.DefaultRequestHeaders.UserAgent.ParseAdd(builder.Configuration[$"{ImageMirrorOptions.Section}:UserAgent"]
            ?? new ImageMirrorOptions().UserAgent);
    })
    .ConfigurePrimaryHttpMessageHandler(SafeHttp.CreateHandler);

var app = builder.Build();

// `dotnet VakifAtlas.Catalog.Api.dll import <file>`: imports a root42-veri.json with its images (Kubernetes Job)
if (args is ["import", var file, ..])
{
    await using var scope = app.Services.CreateAsyncScope();
    var result = await scope.ServiceProvider.GetRequiredService<CatalogFileImport>().ImportFileAsync(file, CancellationToken.None);
    Console.WriteLine($"Imported {result.CreatedWorks + result.UpdatedWorks} works ({result.CreatedWorks} new), " +
                      $"{result.CreatedComplexes + result.UpdatedComplexes} complexes ({result.CreatedComplexes} new); " +
                      $"catalog version {result.CatalogVersion}.");
    return;
}

app.UseServiceDefaults();
app.UseAuthentication();
app.UseAuthorization();
app.UseOutputCache();

app.MapVersionedGroup("catalog").MapWorkEndpoints().MapComplexEndpoints().MapMapEndpoints()
    .WithTags("Catalog").AllowAnonymous();
app.MapAdminGroup("catalog/admin", StaffPolicies.Catalog).MapCatalogAdminEndpoints().WithTags("Catalog (VGM staff)");
app.MapAdminGroup("catalog/admin/imports", StaffPolicies.Catalog, CatalogImportEndpoints.RequestsPerMinute, "catalog-import")
    .MapCatalogImportEndpoints().WithTags("Catalog (VGM staff)");
app.MapDefaultEndpoints();

app.Run();

// for WebApplicationFactory tests
public partial class Program
{
    protected Program()
    {
    }
}
