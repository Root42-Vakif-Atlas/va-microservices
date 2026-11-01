using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VakifAtlas.Catalog.Domain.Works;

namespace VakifAtlas.Catalog.Api.Infrastructure.Persistence.Configurations;

internal class WorkConfiguration : IEntityTypeConfiguration<Work>
{
    public void Configure(EntityTypeBuilder<Work> builder)
    {
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_works_complex_pair", "(complex_id IS NULL) = (complex_position IS NULL)");
            t.HasCheckConstraint("ck_works_complex_position", "complex_position >= 0");
            t.HasCheckConstraint("ck_works_category", $"category IN ({ConfigurationExtensions.InList(WorkCategory.All)})");
            t.HasCheckConstraint("ck_works_period", $"period IN ({ConfigurationExtensions.InList(WorkPeriod.All)})");
        });

        builder.Property(w => w.Key).HasMaxLength(200);
        builder.HasIndex(w => w.Key).IsUnique();
        builder.Property(w => w.District).HasMaxLength(100);
        builder.Property(w => w.Address).HasMaxLength(500);
        builder.Property(w => w.Category).HasMaxLength(40).HasDefaultValue(WorkCategory.Other);
        builder.Property(w => w.Period).HasMaxLength(40).HasDefaultValue(WorkPeriod.Other);
        builder.Property(w => w.IsActive).HasActiveDefault();
        builder.HasLocation("works");
        builder.HasRevision();
        builder.HasTimestamps();
        builder.HasIndex(w => new { w.UpdatedAt, w.Id }).HasDatabaseName("ix_works_updated_at_id");

        builder.HasOne(w => w.City).WithMany().HasForeignKey(w => w.CityCode).OnDelete(DeleteBehavior.NoAction);
        builder.HasIndex(w => w.CityCode).HasDatabaseName("ix_works_city");

        // a work's city must equal its complex's city (MATCH SIMPLE: skipped while complex_id is NULL)
        builder.HasOne(w => w.Complex).WithMany(c => c.Works)
            .HasForeignKey(w => new { w.ComplexId, w.CityCode })
            .HasPrincipalKey(c => new { c.Id, c.CityCode })
            .HasConstraintName("fk_works_complex_city")
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasIndex(w => new { w.ComplexId, w.ComplexPosition }).IsUnique().HasDatabaseName("uq_works_complex_position");

        builder.HasMany(w => w.Translations).WithOne().HasForeignKey(t => t.WorkId);
        builder.HasMany(w => w.Images).WithOne().HasForeignKey(i => i.WorkId);
        builder.HasMany(w => w.Parts).WithOne().HasForeignKey(p => p.WorkId);
    }
}

internal class WorkTranslationConfiguration : IEntityTypeConfiguration<WorkTranslation>
{
    public void Configure(EntityTypeBuilder<WorkTranslation> builder)
    {
        builder.ToTable("work_translations", t => t.HasCheckConstraint("ck_work_translations_tr_no_slug", "locale <> 'tr' OR slug IS NULL"));

        builder.HasKey(t => new { t.WorkId, t.Locale });
        builder.HasLocale();
        builder.Property(t => t.Slug).HasMaxLength(200);
        builder.Property(t => t.Title).HasMaxLength(200).UseCollation(CatalogDbContext.TurkishCollation);
        builder.Property(t => t.Nickname).HasMaxLength(200);
        builder.Property(t => t.ShortDescriptionTitle).HasMaxLength(200);
        builder.Property(t => t.ShortDescription).HasMaxLength(1000);
        builder.Property(t => t.ConstructionDate).HasMaxLength(200);
        builder.Property(t => t.Founder).HasMaxLength(200);
        builder.Property(t => t.Architect).HasMaxLength(200);
        builder.HasTimestamps();

        // en slug unique per language (en slugs never equal a key, so no clash with tr URLs)
        builder.HasIndex(t => new { t.Locale, t.Slug }).IsUnique().HasFilter("slug IS NOT NULL")
            .HasDatabaseName("ux_work_translations_locale_slug");
    }
}

internal class WorkImageConfiguration : IEntityTypeConfiguration<WorkImage>
{
    public void Configure(EntityTypeBuilder<WorkImage> builder)
    {
        builder.ToTable("work_images", t => t.HasCheckConstraint("ck_work_images_position", "position >= 0"));
        builder.HasIndex(i => new { i.WorkId, i.Position }).IsUnique();
        builder.HasOne(i => i.Media).WithMany().HasForeignKey(i => i.MediaId).OnDelete(DeleteBehavior.NoAction);
        builder.HasIndex(i => i.MediaId).HasDatabaseName("ix_work_images_media");
        builder.HasMany(i => i.Translations).WithOne().HasForeignKey(t => t.WorkImageId);
    }
}

internal class WorkImageTranslationConfiguration : IEntityTypeConfiguration<WorkImageTranslation>
{
    public void Configure(EntityTypeBuilder<WorkImageTranslation> builder)
    {
        builder.ToTable("work_image_translations");
        builder.HasKey(t => new { t.WorkImageId, t.Locale });
        builder.HasLocale();
        builder.Property(t => t.AltText).HasMaxLength(150);
        builder.Property(t => t.Title).HasMaxLength(200);
    }
}
