using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VakifAtlas.Catalog.Domain.Works;

namespace VakifAtlas.Catalog.Api.Infrastructure.Persistence.Configurations;

internal class WorkPartConfiguration : IEntityTypeConfiguration<WorkPart>
{
    public void Configure(EntityTypeBuilder<WorkPart> builder)
    {
        builder.ToTable(t => t.HasCheckConstraint("ck_work_parts_position", "position >= 0"));
        builder.HasIndex(p => new { p.WorkId, p.Position }).IsUnique();
        builder.HasMany(p => p.Translations).WithOne().HasForeignKey(t => t.PartId);
        builder.HasMany(p => p.Images).WithOne().HasForeignKey(i => i.PartId);
    }
}

internal class WorkPartTranslationConfiguration : IEntityTypeConfiguration<WorkPartTranslation>
{
    public void Configure(EntityTypeBuilder<WorkPartTranslation> builder)
    {
        builder.ToTable("work_part_translations");
        builder.HasKey(t => new { t.PartId, t.Locale });
        builder.HasLocale();
        builder.Property(t => t.Title).HasMaxLength(200);
    }
}

internal class WorkPartImageConfiguration : IEntityTypeConfiguration<WorkPartImage>
{
    public void Configure(EntityTypeBuilder<WorkPartImage> builder)
    {
        builder.ToTable("work_part_images", t => t.HasCheckConstraint("ck_work_part_images_position", "position >= 0"));
        builder.HasIndex(i => new { i.PartId, i.Position }).IsUnique();
        builder.HasOne(i => i.Media).WithMany().HasForeignKey(i => i.MediaId).OnDelete(DeleteBehavior.NoAction);
        builder.HasIndex(i => i.MediaId).HasDatabaseName("ix_work_part_images_media");
        builder.HasMany(i => i.Translations).WithOne().HasForeignKey(t => t.PartImageId);
    }
}

internal class WorkPartImageTranslationConfiguration : IEntityTypeConfiguration<WorkPartImageTranslation>
{
    public void Configure(EntityTypeBuilder<WorkPartImageTranslation> builder)
    {
        builder.ToTable("work_part_image_translations");
        builder.HasKey(t => new { t.PartImageId, t.Locale });
        builder.HasLocale();
        builder.Property(t => t.AltText).HasMaxLength(150);
        builder.Property(t => t.Title).HasMaxLength(200);
    }
}
