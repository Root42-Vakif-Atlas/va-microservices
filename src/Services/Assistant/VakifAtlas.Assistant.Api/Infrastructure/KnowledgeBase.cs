using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VakifAtlas.Assistant.Domain;
using VakifAtlas.Messaging;
using VakifAtlas.Persistence;

namespace VakifAtlas.Assistant.Api.Infrastructure;

/// <summary>Bilgi tabanı kaydı: eserin iki dilli belgesi (RAG indeksi; ileride gömme vektörü bu tabloya eklenir).</summary>
public sealed class KnowledgeDocument
{
    public required string MonumentId { get; init; }

    public required string Document { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class AssistantDbContext(DbContextOptions<AssistantDbContext> options) : DbContext(options)
{
    public DbSet<KnowledgeDocument> Knowledge => Set<KnowledgeDocument>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddMessagingTables();
        modelBuilder.Entity<KnowledgeDocument>(entity =>
        {
            entity.ToTable("knowledge_documents");
            entity.HasKey(d => d.MonumentId);
            entity.Property(d => d.MonumentId).HasMaxLength(120);
            entity.Property(d => d.Document).HasColumnType("jsonb");
        });
    }
}

internal sealed class AssistantDesignTimeFactory : DesignTimeDbContextFactory<AssistantDbContext>
{
    protected override AssistantDbContext Create(DbContextOptions<AssistantDbContext> options) => new(options);
}

/// <summary>
/// Bellekteki arama motoru: bilgi tabanı değişince (yerel olay) veya en geç dakikada bir
/// (diğer replikaların eşitlemesi) yeniden yüklenir.
/// </summary>
public sealed class KnowledgeBase(IServiceScopeFactory scopes, TimeProvider clock) : IDisposable
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private (GuideEngine Engine, DateTimeOffset LoadedAt)? _current;

    public async Task<GuideEngine> EngineAsync(CancellationToken cancellationToken)
    {
        if (_current is { } current && clock.GetUtcNow() - current.LoadedAt < MaxAge)
        {
            return current.Engine;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_current is { } fresh && clock.GetUtcNow() - fresh.LoadedAt < MaxAge)
            {
                return fresh.Engine;
            }

            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AssistantDbContext>();
            var documents = await db.Knowledge.AsNoTracking().OrderBy(d => d.MonumentId).Select(d => d.Document)
                .ToListAsync(cancellationToken);
            var engine = new GuideEngine(documents.Select(d => JsonSerializer.Deserialize<GuideMonument>(d, Json)!).ToArray());
            _current = (engine, clock.GetUtcNow());
            return engine;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Invalidate() => _current = null;

    public void Dispose() => _lock.Dispose();
}

/// <summary>Katalog detayını iki dilde okuyup bilgi tabanı belgesine çevirir.</summary>
public sealed class CatalogKnowledgeClient(HttpClient http)
{
    public async Task<GuideMonument?> FetchAsync(string monumentId, CancellationToken cancellationToken)
    {
        var tr = await DetailAsync(monumentId, "tr", cancellationToken);
        var en = await DetailAsync(monumentId, "en", cancellationToken);
        if (tr is null || en is null)
        {
            return null;
        }

        return new GuideMonument(tr.Id, new Text(tr.Name, en.Name), tr.Category, tr.Period, tr.City, tr.District, tr.Address,
            new Text(tr.ShortDescription, en.ShortDescription), new Text(tr.History, en.History),
            new Text(tr.ConstructionDate, en.ConstructionDate), new Text(tr.Founder, en.Founder),
            tr.Architect is null ? null : new Text(tr.Architect, en.Architect ?? tr.Architect), tr.IsUnescoWorldHeritage);
    }

    /// <summary>Tüm kimlikler (<c>view=location</c> sayfaları; ilk eşitleme).</summary>
    public async IAsyncEnumerable<string> AllIdsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? cursor = null;
        do
        {
            var page = await http.GetFromJsonAsync<IdPage>(
                $"catalog/monuments?view=location&limit=2000{(cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}")}",
                cancellationToken) ?? throw new InvalidOperationException("Empty catalog page.");
            foreach (var item in page.Items)
            {
                yield return item.Id;
            }

            cursor = page.NextCursor;
        }
        while (cursor is not null);
    }

    private async Task<Detail?> DetailAsync(string id, string language, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"catalog/monuments/{Uri.EscapeDataString(id)}");
        request.Headers.AcceptLanguage.ParseAdd(language);
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<Detail>(cancellationToken);
    }

    private sealed record IdItem(string Id);

    private sealed record IdPage(List<IdItem> Items, string? NextCursor);

    private sealed record Detail(string Id, string Name, string Category, string Period, string City, string District, string Address,
        string ShortDescription, string History, string ConstructionDate, string Founder, string? Architect, bool IsUnescoWorldHeritage);
}

/// <summary>Bilgi tabanı yazımı (olay işleyicisi, ilk eşitleme ve dosya seed'i ortak).</summary>
public sealed class KnowledgeWriter(AssistantDbContext db, KnowledgeBase knowledge, TimeProvider clock)
{
    public async Task UpsertAsync(GuideMonument monument, CancellationToken cancellationToken)
    {
        var document = JsonSerializer.Serialize(monument, KnowledgeBase.Json);
        var existing = await db.Knowledge.FindAsync([monument.Id], cancellationToken);
        if (existing is null)
        {
            db.Knowledge.Add(new KnowledgeDocument { MonumentId = monument.Id, Document = document, UpdatedAt = clock.GetUtcNow() });
        }
        else
        {
            existing.Document = document;
            existing.UpdatedAt = clock.GetUtcNow();
        }

        await db.SaveChangesAsync(cancellationToken);
        knowledge.Invalidate();
    }

    public async Task RemoveAsync(string monumentId, CancellationToken cancellationToken)
    {
        await db.Knowledge.Where(d => d.MonumentId == monumentId).ExecuteDeleteAsync(cancellationToken);
        knowledge.Invalidate();
    }
}

public sealed class AssistantOptions
{
    public const string Section = "Assistant";

    /// <summary>Bilgi tabanı boşsa açılışta katalogdan tümünü eşitle.</summary>
    public bool BootstrapFromCatalog { get; set; } = true;

    /// <summary>İsteğe bağlı: katalog yerine <c>seed/catalog.json</c>'dan doldur (test/çevrimdışı dev).</summary>
    public string? SeedPath { get; set; }
}

/// <summary>Bilgi tabanı boşsa doldurur (seed dosyası veya katalogdan ilk eşitleme).</summary>
internal sealed class KnowledgeBootstrap(
    IServiceScopeFactory scopes,
    Microsoft.Extensions.Options.IOptions<AssistantOptions> options,
    IHostEnvironment environment,
    ILogger<KnowledgeBootstrap> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        for (var attempt = 1; attempt <= 30 && !stoppingToken.IsCancellationRequested; attempt++)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AssistantDbContext>();
                if (await db.Knowledge.AnyAsync(stoppingToken))
                {
                    return;
                }

                var writer = scope.ServiceProvider.GetRequiredService<KnowledgeWriter>();
                if (options.Value.SeedPath is { } seedPath)
                {
                    var path = Path.IsPathRooted(seedPath) ? seedPath : Path.Combine(environment.ContentRootPath, seedPath);
                    path = File.Exists(path) ? path : Path.Combine(AppContext.BaseDirectory, seedPath);
                    foreach (var monument in SeedFile.Read(path))
                    {
                        await writer.UpsertAsync(monument, stoppingToken);
                    }

                    return;
                }

                if (!options.Value.BootstrapFromCatalog)
                {
                    return;
                }

                var catalog = scope.ServiceProvider.GetRequiredService<CatalogKnowledgeClient>();
                var count = 0;
                await foreach (var id in catalog.AllIdsAsync(stoppingToken))
                {
                    if (await catalog.FetchAsync(id, stoppingToken) is { } monument)
                    {
                        await writer.UpsertAsync(monument, stoppingToken);
                        count++;
                    }
                }

                logger.LogInformation("Knowledge base bootstrapped with {Count} monuments", count);
                return;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !stoppingToken.IsCancellationRequested)
            {
                logger.LogInformation("Catalog not ready for knowledge bootstrap (attempt {Attempt})", attempt);
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }
}

internal static class SeedFile
{
    public static IReadOnlyList<GuideMonument> Read(string path) =>
        JsonSerializer.Deserialize<Seed>(File.ReadAllText(path), KnowledgeBase.Json)!.Monuments.Select(m => new GuideMonument(
            m.Id, m.Name, m.Category, m.Period, m.City, m.District, m.Address, m.ShortDescription, m.History, m.ConstructionDate,
            m.Founder, m.Architect, m.IsUnescoWorldHeritage)).ToArray();

    private sealed record Seed(List<SeedMonument> Monuments);

    private sealed record SeedMonument(string Id, Text Name, string Category, string Period, string City, string District,
        string Address, Text ShortDescription, Text History, Text ConstructionDate, Text Founder, Text? Architect,
        bool IsUnescoWorldHeritage);
}

/// <summary>Katalog eser değişikliği → bilgi tabanı (RAG indeksleme).</summary>
public sealed class MonumentUpsertedHandler(CatalogKnowledgeClient catalog, KnowledgeWriter writer)
    : IIntegrationEventHandler<Contracts.MonumentUpserted>
{
    public async Task HandleAsync(Contracts.MonumentUpserted integrationEvent, CancellationToken cancellationToken)
    {
        var monument = await catalog.FetchAsync(integrationEvent.MonumentId, cancellationToken);
        if (monument is null)
        {
            await writer.RemoveAsync(integrationEvent.MonumentId, cancellationToken);
        }
        else
        {
            await writer.UpsertAsync(monument, cancellationToken);
        }
    }
}
