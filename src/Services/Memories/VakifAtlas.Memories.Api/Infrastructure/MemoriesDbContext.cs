using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using VakifAtlas.Media;
using VakifAtlas.Memories.Domain;
using VakifAtlas.Messaging;
using VakifAtlas.Persistence;

namespace VakifAtlas.Memories.Api.Infrastructure;

public sealed class MemoriesDbContext(DbContextOptions<MemoriesDbContext> options) : DbContext(options)
{
    private static readonly JsonSerializerOptions ImageJson = new(JsonSerializerDefaults.Web);

    public DbSet<Memory> Memories => Set<Memory>();

    public DbSet<MemoryReport> Reports => Set<MemoryReport>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddMessagingTables();
        modelBuilder.AddPhotoUploads();

        modelBuilder.Entity<Memory>(entity =>
        {
            entity.ToTable("memories");
            entity.HasKey(m => m.Id);
            entity.Property(m => m.Id).HasMaxLength(40);
            entity.Property(m => m.MonumentId).HasMaxLength(120);
            entity.Property(m => m.AuthorId).HasMaxLength(40);
            entity.Property(m => m.AuthorName).HasMaxLength(120);
            entity.Property(m => m.Status).HasMaxLength(20);
            entity.OwnsMany(m => m.Photos, photos => photos.ToJson("photos"));
            // Zaman çizelgesi keyset'i: eser + anı tarihi, paylaşım zamanı, kimlik (azalan).
            entity.HasIndex(m => new { m.MonumentId, m.MemoryDate, m.CreatedAt, m.Id }).IsDescending(false, true, true, true);
            entity.HasIndex(m => new { m.AuthorId, m.CreatedAt, m.Id }).IsDescending(false, true, true);
            entity.Property(m => m.ModerationReason).HasMaxLength(40);
            entity.Property(m => m.ModerationNote).HasMaxLength(MemoryRules.MaxModerationNoteLength * 2);
            entity.Property(m => m.ModeratedBy).HasMaxLength(40);
            entity.Property(m => m.ModeratedByName).HasMaxLength(120);
            // VGM paneli: moderasyon kuyruğu (duruma göre) ve şikâyet kuyruğu (açık bildirim sayısı).
            entity.HasIndex(m => new { m.Status, m.CreatedAt });
            entity.HasIndex(m => m.ReportCount).HasFilter("report_count > 0");
            // YZ moderasyonu (ADR 58): skorlar jsonb; metrikler karar/zamana, kuyruk sıralaması skora göre.
            entity.Property(m => m.AiVerdict).HasMaxLength(20);
            entity.Property(m => m.AiTopCategory).HasMaxLength(40);
            entity.Property(m => m.AiReviewCause).HasMaxLength(20);
            entity.Property(m => m.AiModelVersion).HasMaxLength(40);
            entity.Property(m => m.AiScores).HasColumnType("jsonb").HasConversion(
                v => v == null ? null : JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => v == null ? null : JsonSerializer.Deserialize<Dictionary<string, double>>(v, (JsonSerializerOptions?)null),
                new ValueComparer<Dictionary<string, double>?>(
                    (a, b) => a == null ? b == null : b != null && a.Count == b.Count && !a.Except(b).Any(),
                    v => v == null ? 0 : v.Count,
                    v => v == null ? null : new Dictionary<string, double>(v)));
            entity.HasIndex(m => new { m.AiVerdict, m.CreatedAt }).HasFilter("ai_verdict IS NOT NULL");
            entity.HasIndex(m => new { m.Status, m.AiScore });
            // Görsel moderasyon: fotoğraf başına NSFW + eser benzerliği (jsonb, camelCase).
            entity.Property(m => m.AiVisionVersion).HasMaxLength(80);
            entity.Property(m => m.AiImages).HasColumnType("jsonb").HasConversion(
                v => v == null ? null : JsonSerializer.Serialize(v, ImageJson),
                v => v == null ? null : JsonSerializer.Deserialize<List<AiImageScore>>(v, ImageJson),
                new ValueComparer<List<AiImageScore>?>(
                    (a, b) => a == null ? b == null : b != null && a.SequenceEqual(b),
                    v => v == null ? 0 : v.Count,
                    v => v == null ? null : v.ToList()));
            // Spam kuralları + metnin eserle bağlamı.
            entity.Property(m => m.AiSpamSignals).HasColumnType("text[]");
            entity.Property(m => m.AiContextVersion).HasMaxLength(80);
        });

        modelBuilder.Entity<MemoryReport>(entity =>
        {
            entity.ToTable("memory_reports");
            entity.HasKey(r => new { r.MemoryId, r.ReporterId });
            entity.HasIndex(r => r.ReporterId);
            entity.Property(r => r.Reason).HasMaxLength(40);
            entity.Property(r => r.Note).HasMaxLength(2000);
            entity.Property(r => r.Resolution).HasMaxLength(20).HasDefaultValue(ReportResolution.Open).HasSentinel(string.Empty);
        });
    }
}

internal sealed class MemoriesDesignTimeFactory : DesignTimeDbContextFactory<MemoriesDbContext>
{
    protected override MemoriesDbContext Create(DbContextOptions<MemoriesDbContext> options) => new(options);
}
