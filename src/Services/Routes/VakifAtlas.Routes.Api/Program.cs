using Microsoft.Extensions.Options;
using VakifAtlas.CatalogClient;
using VakifAtlas.Contracts;
using VakifAtlas.Messaging;
using VakifAtlas.Persistence;
using VakifAtlas.Routes.Api.Features;
using VakifAtlas.Routes.Api.Infrastructure;
using VakifAtlas.ServiceDefaults;
using VakifAtlas.ServiceDefaults.Auth;
using VakifAtlas.ServiceDefaults.Redis;
using VakifAtlas.ServiceDefaults.Versioning;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults("routes");
builder.AddRedis();
builder.AddJwtAuthentication();
builder.AddMessaging<RoutesDbContext>("routes")
    .Subscribe<AccountDeleted, AccountDeletedHandler>();
builder.AddPostgres<RoutesDbContext>();
builder.AddCatalogClient();

builder.Services.Configure<RoutingOptions>(builder.Configuration.GetSection(RoutingOptions.Section));
builder.Services.AddScoped<LegCalculator>();
if (builder.Configuration[$"{RoutingOptions.Section}:Engine"] == "osrm")
{
    builder.Services.AddHttpClient<IRoutingEngine, OsrmRoutingEngine>((services, http) =>
            http.BaseAddress = new Uri(services.GetRequiredService<IOptions<RoutingOptions>>().Value.OsrmUrl))
        .AddStandardResilienceHandler();
}
else
{
    builder.Services.AddSingleton<IRoutingEngine, FixtureRoutingEngine>();
}

var app = builder.Build();

app.UseServiceDefaults();
app.UseAuthentication();
app.UseAuthorization();

app.MapVersionedGroup("routes").MapRouteEndpoints().WithTags("Routes");
app.MapVersionedGroup("saved-monuments").MapSavedMonumentEndpoints().WithTags("Saved monuments");
app.MapDefaultEndpoints();

app.Run();

/// <summary>WebApplicationFactory testleri için.</summary>
public partial class Program
{
    protected Program()
    {
    }
}
