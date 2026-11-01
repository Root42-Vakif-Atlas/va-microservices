using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using VakifAtlas.Identity.Domain;
using VakifAtlas.Messaging;
using VakifAtlas.Persistence;

namespace VakifAtlas.Identity.Api.Infrastructure;

public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    public DbSet<OneTimeCode> Codes => Set<OneTimeCode>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<LegalDocument> LegalDocuments => Set<LegalDocument>();

    public DbSet<EmailMessage> Emails => Set<EmailMessage>();

    public DbSet<StoredSigningKey> SigningKeys => Set<StoredSigningKey>();

    public DbSet<MfaChallenge> MfaChallenges => Set<MfaChallenge>();

    public DbSet<RecoveryCode> RecoveryCodes => Set<RecoveryCode>();

    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddMessagingTables();

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(u => u.Id);
            entity.Property(u => u.Id).HasMaxLength(40);
            entity.Property(u => u.Email).HasMaxLength(Credentials.MaxEmailLength);
            entity.HasIndex(u => u.Email).IsUnique();
            entity.Property(u => u.DisplayName).HasMaxLength(Credentials.MaxDisplayNameLength * 2);
            entity.Property(u => u.PasswordHash).HasMaxLength(200);
            entity.Property(u => u.FailedLoginAttempts);
            entity.Property(u => u.LockedUntil);
            entity.Property(u => u.TermsVersion).HasMaxLength(40);
            entity.Property(u => u.PrivacyNoticeVersion).HasMaxLength(40);
            entity.Property(u => u.MfaSecret).HasMaxLength(200);
            entity.HasIndex(u => u.Roles).HasMethod("gin");
        });

        modelBuilder.Entity<MfaChallenge>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.HasIndex(c => c.TokenHash).IsUnique();
            entity.HasIndex(c => c.ExpiresAt);
            entity.Property(c => c.UserId).HasMaxLength(40);
            entity.Property(c => c.TokenHash).HasMaxLength(100);
            entity.Property(c => c.Purpose).HasConversion<string>().HasMaxLength(20);
            entity.Property(c => c.PendingSecret).HasMaxLength(200);
        });

        modelBuilder.Entity<RecoveryCode>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.HasIndex(c => new { c.UserId, c.CodeHash }).IsUnique();
            entity.Property(c => c.UserId).HasMaxLength(40);
            entity.Property(c => c.CodeHash).HasMaxLength(100);
        });

        modelBuilder.Entity<AuditEntry>(entity =>
        {
            entity.ToTable("audit_log");
            entity.HasKey(a => a.Id);
            entity.HasIndex(a => new { a.At, a.Id });
            entity.HasIndex(a => new { a.ActorId, a.At });
            entity.HasIndex(a => new { a.TargetType, a.TargetId });
            entity.Property(a => a.ActorId).HasMaxLength(40);
            entity.Property(a => a.Service).HasMaxLength(40);
            entity.Property(a => a.Action).HasMaxLength(80);
            entity.Property(a => a.TargetType).HasMaxLength(40);
            entity.Property(a => a.TargetId).HasMaxLength(80);
            entity.Property(a => a.Changes).HasColumnType("jsonb").HasConversion(
                value => JsonSerializer.Serialize(value, (JsonSerializerOptions?)null),
                json => JsonSerializer.Deserialize<Dictionary<string, string?>>(json, (JsonSerializerOptions?)null) ?? new(),
                new ValueComparer<Dictionary<string, string?>>(
                    (a, b) => a!.Count == b!.Count && !a.Except(b).Any(),
                    value => value.Count,
                    value => new Dictionary<string, string?>(value)));
            entity.Property(a => a.CorrelationId).HasMaxLength(64);
        });

        modelBuilder.Entity<OneTimeCode>(entity =>
        {
            entity.HasKey(c => new { c.Email, c.Purpose });
            entity.Property(c => c.Purpose).HasConversion<string>().HasMaxLength(20);
            entity.Property(c => c.CodeHash).HasMaxLength(100);
            entity.Property(c => c.Attempts);
        });

        modelBuilder.Entity<RefreshToken>(entity =>
        {
            entity.HasKey(t => t.Id);
            entity.HasIndex(t => t.TokenHash).IsUnique();
            entity.HasIndex(t => new { t.UserId, t.FamilyId });
            entity.Property(t => t.TokenHash).HasMaxLength(100);
            entity.Property(t => t.Kind).HasConversion<string>().HasMaxLength(20).HasDefaultValue(SessionKind.Citizen)
                .HasSentinel((SessionKind)(-1));
        });

        modelBuilder.Entity<LegalDocument>(entity =>
        {
            entity.HasKey(d => new { d.Type, d.Language });
            entity.Property(d => d.Type).HasMaxLength(40);
            entity.Property(d => d.Language).HasMaxLength(5);
            entity.Property(d => d.Version).HasMaxLength(40);
        });

        modelBuilder.Entity<EmailMessage>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Kind).HasConversion<string>().HasMaxLength(40);
            entity.Property(e => e.To).HasMaxLength(Credentials.MaxEmailLength);
            entity.HasIndex(e => e.CreatedAt).HasFilter("sent_at IS NULL");
        });

        modelBuilder.Entity<StoredSigningKey>(entity => entity.HasKey(k => k.KeyId));
    }
}

internal sealed class IdentityDesignTimeFactory : DesignTimeDbContextFactory<IdentityDbContext>
{
    protected override IdentityDbContext Create(DbContextOptions<IdentityDbContext> options) => new(options);
}
