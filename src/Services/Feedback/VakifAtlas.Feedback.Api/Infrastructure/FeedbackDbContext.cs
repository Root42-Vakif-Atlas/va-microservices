using Microsoft.EntityFrameworkCore;
using VakifAtlas.Feedback.Domain;
using VakifAtlas.Messaging;
using VakifAtlas.Persistence;

namespace VakifAtlas.Feedback.Api.Infrastructure;

public sealed class FeedbackDbContext(DbContextOptions<FeedbackDbContext> options) : DbContext(options)
{
    public DbSet<FeedbackEntry> Entries => Set<FeedbackEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddMessagingTables();
        modelBuilder.Entity<FeedbackEntry>(entity =>
        {
            entity.ToTable("feedback");
            entity.HasKey(f => f.Id);
            entity.Property(f => f.Id).HasMaxLength(40);
            entity.Property(f => f.Category).HasMaxLength(20);
            entity.Property(f => f.Message).HasMaxLength(FeedbackRules.MaxMessage * 2);
            entity.Property(f => f.AppVersion).HasMaxLength(40);
            entity.Property(f => f.Platform).HasMaxLength(20);
            entity.Property(f => f.Language).HasMaxLength(10);
            entity.Property(f => f.UserId).HasMaxLength(40);
            entity.HasIndex(f => new { f.CreatedAt, f.Id }).IsDescending(true, true);
            entity.HasIndex(f => f.UserId);
            entity.Property(f => f.Status).HasMaxLength(20).HasDefaultValue(FeedbackStatus.New).HasSentinel(string.Empty);
            entity.Property(f => f.StaffNote).HasMaxLength(FeedbackRules.MaxStaffNote * 2);
            entity.Property(f => f.HandledBy).HasMaxLength(40);
            entity.Property(f => f.HandledByName).HasMaxLength(120);
            entity.HasIndex(f => new { f.Status, f.CreatedAt });
        });
    }
}

internal sealed class FeedbackDesignTimeFactory : DesignTimeDbContextFactory<FeedbackDbContext>
{
    protected override FeedbackDbContext Create(DbContextOptions<FeedbackDbContext> options) => new(options);
}
