using VakifAtlas.Contracts;
using VakifAtlas.Feedback.Api.Features;
using VakifAtlas.Feedback.Api.Infrastructure;
using VakifAtlas.Messaging;
using VakifAtlas.Persistence;
using VakifAtlas.ServiceDefaults;
using VakifAtlas.ServiceDefaults.Admin;
using VakifAtlas.ServiceDefaults.Auth;
using VakifAtlas.ServiceDefaults.Redis;
using VakifAtlas.ServiceDefaults.Versioning;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults("feedback");
builder.AddRedis();
builder.AddJwtAuthentication();
builder.AddMessaging<FeedbackDbContext>("feedback")
    .Subscribe<AccountDeleted, AccountDeletedHandler>();
builder.AddPostgres<FeedbackDbContext>();

var app = builder.Build();

app.UseServiceDefaults();
app.UseAuthentication();
app.UseAuthorization();

app.MapVersionedGroup("feedback").MapFeedbackEndpoints().WithTags("Feedback");
app.MapAdminGroup("feedback/admin", StaffPolicies.Feedback).MapStaffEndpoints().WithTags("Feedback (VGM staff)");
app.MapDefaultEndpoints();

app.Run();

/// <summary>WebApplicationFactory testleri için.</summary>
public partial class Program
{
    protected Program()
    {
    }
}
