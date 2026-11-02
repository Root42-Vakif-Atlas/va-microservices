using Microsoft.EntityFrameworkCore;
using VakifAtlas.Messaging;
using VakifAtlas.Persistence;
using VakifAtlas.Routes.Domain;

namespace VakifAtlas.Routes.Api.Infrastructure;

public sealed class RoutesDbContext(DbContextOptions<RoutesDbContext> options) : DbContext(options)
{
    public DbSet<TravelRoute> Routes => Set<TravelRoute>();

    public DbSet<CachedLeg> Legs => Set<CachedLeg>();

    public DbSet<SavedMonument> SavedMonuments => Set<SavedMonument>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddMessagingTables();

        modelBuilder.Entity<TravelRoute>(entity =>
        {
            entity.ToTable("routes");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).HasMaxLength(40);
            entity.Property(r => r.UserId).HasMaxLength(40);
            entity.Property(r => r.Name).HasMaxLength(RoutePlanRules.MaxNameLength * 2);
            entity.Property(r => r.TravelMode).HasMaxLength(20);
            entity.Property(r => r.LegsEngine).HasMaxLength(40);
            entity.Ignore(r => r.Summary);
            entity.HasIndex(r => new { r.InstallationId, r.CreatedAt });
            entity.HasIndex(r => r.UserId).HasFilter("user_id IS NOT NULL");
            entity.OwnsMany(r => r.Stops, stops =>
            {
                stops.ToJson("stops");
                stops.OwnsMany(s => s.Breaks);
            });
            entity.OwnsMany(r => r.Legs, legs => legs.ToJson("legs"));
        });

        modelBuilder.Entity<CachedLeg>(entity =>
        {
            entity.ToTable("leg_cache");
            entity.HasKey(l => new { l.FromId, l.ToId, l.Engine });
            entity.Property(l => l.Engine).HasMaxLength(40);
        });

        modelBuilder.Entity<SavedMonument>(entity =>
        {
            entity.ToTable("saved_monuments");
            entity.HasKey(s => new { s.OwnerKey, s.MonumentId });
            entity.Property(s => s.OwnerKey).HasMaxLength(SavedMonument.MaxOwnerKeyLength);
            entity.Property(s => s.MonumentId).HasMaxLength(SavedMonumentRules.MaxIdLength);
        });
    }
}

/// <summary>Yönlendirme motorunun hesapladığı bacak (eser çifti + motor/veri sürümü başına).</summary>
public sealed class CachedLeg
{
    public required string FromId { get; init; }

    public required string ToId { get; init; }

    public required string Engine { get; init; }

    public int DistanceMeters { get; init; }

    public int DurationSeconds { get; init; }

    public required string Geometry { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

internal sealed class RoutesDesignTimeFactory : DesignTimeDbContextFactory<RoutesDbContext>
{
    protected override RoutesDbContext Create(DbContextOptions<RoutesDbContext> options) => new(options);
}
