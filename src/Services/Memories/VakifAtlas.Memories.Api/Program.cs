using VakifAtlas.CatalogClient;
using VakifAtlas.Contracts;
using VakifAtlas.Media;
using VakifAtlas.Memories.Api.Features;
using VakifAtlas.Memories.Api.Infrastructure;
using VakifAtlas.Messaging;
using VakifAtlas.Persistence;
using VakifAtlas.ServiceDefaults;
using VakifAtlas.ServiceDefaults.Admin;
using VakifAtlas.ServiceDefaults.Auth;
using VakifAtlas.ServiceDefaults.Redis;
using VakifAtlas.ServiceDefaults.Versioning;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults("memories");
builder.AddRedis();
builder.AddJwtAuthentication();
builder.AddMessaging<MemoriesDbContext>("memories")
    .Subscribe<AccountDeleted, AccountDeletedHandler>();
builder.AddPostgres<MemoriesDbContext>();
builder.AddCatalogClient();
builder.AddObjectStorage();

builder.Services.AddPhotoUploads<MemoriesDbContext>();
builder.AddMemoryModeration();
builder.Services.AddDataSeeder<MemorySeeder>();

var app = builder.Build();

app.UseServiceDefaults();
app.UseAuthentication();
app.UseAuthorization();

app.MapVersionedGroup("memories").MapMemoryEndpoints().WithTags("Memories");
app.MapAdminGroup("memories/admin", StaffPolicies.Memories).MapModerationEndpoints().WithTags("Memories (VGM moderation)");
app.MapDefaultEndpoints();

app.Run();

/// <summary>WebApplicationFactory testleri için.</summary>
public partial class Program
{
    protected Program()
    {
    }
}
