using VakifAtlas.Assistant.Api.Features;
using VakifAtlas.Assistant.Api.Infrastructure;
using VakifAtlas.Contracts;
using VakifAtlas.Messaging;
using VakifAtlas.Persistence;
using VakifAtlas.ServiceDefaults;
using VakifAtlas.ServiceDefaults.Redis;
using VakifAtlas.ServiceDefaults.Versioning;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults("assistant");
builder.AddRedis();
builder.AddMessaging<AssistantDbContext>("assistant")
    .Subscribe<MonumentUpserted, MonumentUpsertedHandler>();
builder.AddPostgres<AssistantDbContext>();

builder.Services.Configure<AssistantOptions>(builder.Configuration.GetSection(AssistantOptions.Section));
builder.Services.AddSingleton<KnowledgeBase>();
builder.Services.AddScoped<KnowledgeWriter>();
builder.Services.AddSingleton<IConversationStore, RedisConversationStore>();
builder.Services.AddSingleton<IAnswerGenerator, RuleBasedAnswerGenerator>();
builder.Services.AddHostedService<KnowledgeBootstrap>();
builder.Services
    .AddHttpClient<CatalogKnowledgeClient>(http =>
        http.BaseAddress = new Uri(builder.Configuration["Services:Catalog"] ?? "http://catalog:8080/v1/"))
    .AddStandardResilienceHandler();

var app = builder.Build();

app.UseServiceDefaults();

app.MapVersionedGroup("assistant").MapAssistantEndpoints().WithTags("Digital guide");
app.MapDefaultEndpoints();

app.Run();

/// <summary>WebApplicationFactory testleri için.</summary>
public partial class Program
{
    protected Program()
    {
    }
}
