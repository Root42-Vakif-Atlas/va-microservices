using Microsoft.EntityFrameworkCore;
using VakifAtlas.Media.Domain;
using VakifAtlas.Messaging;
using VakifAtlas.Persistence;

namespace VakifAtlas.Media.Api.Infrastructure;

/// <summary>Database of the media service: route exports and the messaging outbox/inbox tables.</summary>
/// <param name="options">The context options.</param>
public sealed class MediaDbContext(DbContextOptions<MediaDbContext> options) : DbContext(options)
{
    /// <summary>Route PDF exports.</summary>
    public DbSet<RouteExport> Exports => Set<RouteExport>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddMessagingTables();
        modelBuilder.Entity<RouteExport>(entity =>
        {
            entity.ToTable("route_exports");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(40);
            entity.Property(e => e.UserId).HasMaxLength(40);
            entity.Property(e => e.RouteId).HasMaxLength(60);
            entity.Property(e => e.RouteName).HasMaxLength(RouteExportRules.MaxRouteName * 2);
            entity.Property(e => e.FileName).HasMaxLength(RouteExportRules.MaxFileName * 2);
            entity.Property(e => e.Sha256).HasMaxLength(64);
            entity.Property(e => e.ObjectKey).HasMaxLength(200);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);
            entity.HasIndex(e => new { e.UserId, e.Status, e.CreatedAt, e.Id }).IsDescending(false, false, true, true);
            entity.HasIndex(e => new { e.InstallationId, e.Status, e.CreatedAt, e.Id })
                .IsDescending(false, false, true, true)
                .HasFilter("user_id IS NULL");
        });
    }
}

/// <summary>Lets <c>dotnet ef</c> create the context at design time.</summary>
internal sealed class MediaDesignTimeFactory : DesignTimeDbContextFactory<MediaDbContext>
{
    protected override MediaDbContext Create(DbContextOptions<MediaDbContext> options) => new(options);
}
