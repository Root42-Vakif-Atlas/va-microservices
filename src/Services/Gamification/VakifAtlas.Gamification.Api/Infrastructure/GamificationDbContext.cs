using Microsoft.EntityFrameworkCore;
using VakifAtlas.Gamification.Domain;
using VakifAtlas.Messaging;
using VakifAtlas.Persistence;

namespace VakifAtlas.Gamification.Api.Infrastructure;

public sealed class GamificationDbContext(DbContextOptions<GamificationDbContext> options) : DbContext(options)
{
    public DbSet<Contribution> Contributions => Set<Contribution>();

    public DbSet<Acknowledgement> Acknowledgements => Set<Acknowledgement>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddMessagingTables();

        modelBuilder.Entity<Contribution>(entity =>
        {
            entity.ToTable("contributions");
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Id).ValueGeneratedNever();
            entity.Property(c => c.UserId).HasMaxLength(40);
            entity.Property(c => c.Kind).HasConversion<string>().HasMaxLength(30);
            entity.Property(c => c.ReferenceId).HasMaxLength(60);
            entity.HasIndex(c => new { c.UserId, c.OccurredAt });
        });

        modelBuilder.Entity<Acknowledgement>(entity =>
        {
            entity.ToTable("acknowledgements");
            entity.HasKey(a => new { a.UserId, a.ItemId });
            entity.Property(a => a.UserId).HasMaxLength(40);
            entity.Property(a => a.ItemId).HasMaxLength(64);
        });
    }
}

internal sealed class GamificationDesignTimeFactory : DesignTimeDbContextFactory<GamificationDbContext>
{
    protected override GamificationDbContext Create(DbContextOptions<GamificationDbContext> options) => new(options);
}
