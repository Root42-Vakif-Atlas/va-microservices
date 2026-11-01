using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using VakifAtlas.CivilProtection.Domain;
using VakifAtlas.Media;
using VakifAtlas.Messaging;
using VakifAtlas.Persistence;

namespace VakifAtlas.CivilProtection.Api.Infrastructure;

public sealed class CivilProtectionDbContext(DbContextOptions<CivilProtectionDbContext> options) : DbContext(options)
{
    private static readonly JsonSerializerOptions EvidenceJson = new(JsonSerializerDefaults.Web);

    public DbSet<DamageReport> Reports => Set<DamageReport>();

    public DbSet<ReportLocation> Locations => Set<ReportLocation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddMessagingTables();
        modelBuilder.AddPhotoUploads();

        modelBuilder.Entity<DamageReport>(entity =>
        {
            entity.ToTable("damage_reports");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).HasMaxLength(40);
            entity.Property(r => r.ReferenceCode).HasMaxLength(20);
            entity.HasIndex(r => r.ReferenceCode).IsUnique();
            entity.Property(r => r.OwnerId).HasMaxLength(40);
            entity.Property(r => r.MonumentId).HasMaxLength(120);
            entity.Property(r => r.Category).HasMaxLength(40);
            entity.Property(r => r.Urgency).HasMaxLength(20);
            entity.Property(r => r.Status).HasMaxLength(20);
            entity.Ignore(r => r.IsFinal);
            entity.Ignore(r => r.HasStaffAction);
            entity.OwnsOne(r => r.LocationCheck, check =>
            {
                check.Property(c => c.Verification).HasColumnName("location_verification").HasMaxLength(20);
                check.Property(c => c.DistanceMeters).HasColumnName("location_distance_meters");
            });
            entity.OwnsMany(r => r.Photos, photos => photos.ToJson("photos"));
            entity.OwnsMany(r => r.History, history => history.ToJson("history"));
            entity.HasIndex(r => new { r.OwnerId, r.CreatedAt, r.Id }).IsDescending(false, true, true);
            // VGM paneli: duruma ve aciliyete göre kuyruk.
            entity.HasIndex(r => new { r.Status, r.CreatedAt });
            entity.HasIndex(r => new { r.Urgency, r.CreatedAt });
            entity.HasIndex(r => r.MonumentId);
            // Kanıt etiketi: panel süzgeci/sıralaması; gerekçeler ve ham sinyaller jsonb (camelCase).
            entity.Property(r => r.EvidenceLevel).HasMaxLength(20);
            entity.Property(r => r.EvidenceVersion).HasMaxLength(120);
            entity.HasIndex(r => new { r.EvidenceLevel, r.CreatedAt });
            entity.HasIndex(r => r.CreatedAt).HasFilter("evidence_scored_at IS NULL");
            entity.Property(r => r.EvidenceReasons).HasColumnType("jsonb").HasConversion(
                v => v == null ? null : JsonSerializer.Serialize(v, EvidenceJson),
                v => v == null ? null : JsonSerializer.Deserialize<List<EvidenceReason>>(v, EvidenceJson),
                new ValueComparer<List<EvidenceReason>?>(
                    (a, b) => a == null ? b == null : b != null && a.SequenceEqual(b),
                    v => v == null ? 0 : v.Count,
                    v => v == null ? null : v.ToList()));
            entity.Property(r => r.EvidenceSignals).HasColumnType("jsonb").HasConversion(
                v => v == null ? null : JsonSerializer.Serialize(v, EvidenceJson),
                v => v == null ? null : JsonSerializer.Deserialize<EvidenceSignals>(v, EvidenceJson),
                new ValueComparer<EvidenceSignals?>(
                    (a, b) => ReferenceEquals(a, b),
                    v => v == null ? 0 : v.GetHashCode(),
                    v => v));
        });

        modelBuilder.Entity<ReferenceCounter>(entity =>
        {
            entity.ToTable("reference_counters");
            entity.HasKey(c => c.Year);
            entity.Property(c => c.Year).ValueGeneratedNever();
        });

        modelBuilder.Entity<ReportLocation>(entity =>
        {
            entity.ToTable("report_locations");
            entity.HasKey(l => l.ReportId);
            entity.HasIndex(l => l.DeleteAfter).HasFilter("delete_after IS NOT NULL");
        });
    }
}

internal sealed class CivilProtectionDesignTimeFactory : DesignTimeDbContextFactory<CivilProtectionDbContext>
{
    protected override CivilProtectionDbContext Create(DbContextOptions<CivilProtectionDbContext> options) => new(options);
}

internal sealed class ReferenceCounter
{
    public int Year { get; init; }

    public long Value { get; init; }
}

/// <summary>Yıl başına sıra (<c>SK-2026-000103</c>): atomik sayaç satırı.</summary>
internal static class ReferenceCodes
{
    public static async Task<string> NextAsync(CivilProtectionDbContext db, int year, CancellationToken cancellationToken)
    {
        var value = await db.Database.SqlQuery<long>($"""
            INSERT INTO reference_counters (year, value) VALUES ({year}, 1)
            ON CONFLICT (year) DO UPDATE SET value = reference_counters.value + 1
            RETURNING value AS "Value"
            """).ToListAsync(cancellationToken);
        return DamageReportRules.ReferenceCode(year, value[0]);
    }
}
