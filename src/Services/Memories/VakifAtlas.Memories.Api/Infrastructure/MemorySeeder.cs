using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VakifAtlas.Memories.Domain;
using VakifAtlas.Persistence;

namespace VakifAtlas.Memories.Api.Infrastructure;

/// <summary>Flutter mock'unun 16 kurgusal metin anısı (dev/staging; boşsa).</summary>
internal sealed class MemorySeeder(MemoriesDbContext db, IHostEnvironment environment) : IDataSeeder
{
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var path = new[] { environment.ContentRootPath, AppContext.BaseDirectory }
            .Select(root => Path.Combine(root, "seed", "memories.json")).FirstOrDefault(File.Exists);
        if (path is null || await db.Memories.AnyAsync(cancellationToken))
        {
            return;
        }

        var seed = JsonSerializer.Deserialize<SeedFile>(await File.ReadAllTextAsync(path, cancellationToken),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        foreach (var item in seed.Memories)
        {
            db.Memories.Add(new Memory
            {
                Id = item.Id,
                MonumentId = item.MonumentId,
                AuthorId = item.Author.Id,
                AuthorName = item.Author.DisplayName,
                Text = item.Text,
                VisitedOn = item.VisitedOn,
                CreatedAt = item.CreatedAt,
                MemoryDate = Memory.MemoryDateOf(item.VisitedOn, item.CreatedAt),
                Status = item.Status,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private sealed record SeedFile(List<SeedMemory> Memories);

    private sealed record SeedAuthor(string Id, string DisplayName);

    private sealed record SeedMemory(string Id, string MonumentId, SeedAuthor Author, string Text, DateOnly? VisitedOn,
        DateTimeOffset CreatedAt, string Status);
}
