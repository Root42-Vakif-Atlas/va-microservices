using VakifAtlas.Contracts;
using VakifAtlas.Media;
using VakifAtlas.Media.Api.Features;
using VakifAtlas.Media.Api.Infrastructure;
using VakifAtlas.Messaging;
using VakifAtlas.Persistence;
using VakifAtlas.ServiceDefaults;
using VakifAtlas.ServiceDefaults.Auth;
using VakifAtlas.ServiceDefaults.Redis;
using VakifAtlas.ServiceDefaults.Versioning;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults("media");
builder.AddRedis();
builder.AddJwtAuthentication();
builder.AddMessaging<MediaDbContext>("media")
    .Subscribe<AccountDeleted, AccountDeletedHandler>();
builder.AddPostgres<MediaDbContext>();
builder.AddObjectStorage();
builder.Services.AddHostedService<PendingExportCleanup>();

var app = builder.Build();

app.UseServiceDefaults();
app.UseAuthentication();
app.UseAuthorization();

app.MapVersionedGroup("media").MapPublicMediaEndpoints().WithTags("Media");
app.MapVersionedGroup("route-exports").MapRouteExportEndpoints().WithTags("Route exports");
app.MapDefaultEndpoints();

app.Run();

/// <summary>Entry point marker, exposed for <c>WebApplicationFactory</c> based tests.</summary>
public partial class Program
{
    protected Program()
    {
    }
}
