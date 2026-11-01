using VakifAtlas.Contracts;
using VakifAtlas.Gamification.Api.Features;
using VakifAtlas.Gamification.Api.Infrastructure;
using VakifAtlas.Messaging;
using VakifAtlas.Persistence;
using VakifAtlas.ServiceDefaults;
using VakifAtlas.ServiceDefaults.Auth;
using VakifAtlas.ServiceDefaults.Redis;
using VakifAtlas.ServiceDefaults.Versioning;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults("gamification");
builder.AddRedis();
builder.AddJwtAuthentication();
builder.AddMessaging<GamificationDbContext>("gamification")
    .Subscribe<RouteCreated, ContributionHandlers>()
    .Subscribe<MemoryPublished, ContributionHandlers>()
    .Subscribe<DamageReportSubmitted, ContributionHandlers>()
    .Subscribe<AccountDeleted, ContributionHandlers>();
builder.AddPostgres<GamificationDbContext>();

var app = builder.Build();

app.UseServiceDefaults();
app.UseAuthentication();
app.UseAuthorization();

app.MapVersionedGroup("gamification").MapGamificationEndpoints().WithTags("Gamification");
app.MapDefaultEndpoints();

app.Run();

/// <summary>WebApplicationFactory testleri için.</summary>
public partial class Program
{
    protected Program()
    {
    }
}
