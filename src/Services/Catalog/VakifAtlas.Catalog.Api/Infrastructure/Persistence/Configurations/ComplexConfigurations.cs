using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VakifAtlas.Catalog.Domain.Complexes;

namespace VakifAtlas.Catalog.Api.Infrastructure.Persistence.Configurations;

internal class ComplexConfiguration : IEntityTypeConfiguration<Complex>
{
    public void Configure(EntityTypeBuilder<Complex> builder)
    {
        builder.Property(c => c.Key).HasMaxLength(200);
        builder.HasIndex(c => c.Key).IsUnique();
        builder.Property(c => c.District).HasMaxLength(100);
        builder.Property(c => c.Address).HasMaxLength(500);
        builder.Property(c => c.IsActive).HasActiveDefault();
        builder.HasLocation("complexes");
        builder.HasRevision();
        builder.HasTimestamps();

        // target of the works composite FK
        builder.HasAlternateKey(c => new { c.Id, c.CityCode }).HasName("uq_complexes_id_city");

        builder.HasOne(c => c.City).WithMany().HasForeignKey(c => c.CityCode).OnDelete(DeleteBehavior.NoAction);
        builder.HasIndex(c => c.CityCode).HasDatabaseName("ix_complexes_city");

        builder.HasMany(c => c.Translations).WithOne().HasForeignKey(t => t.ComplexId);
        builder.HasMany(c => c.Images).WithOne().HasForeignKey(i => i.ComplexId);
    }
}

internal class ComplexTranslationConfiguration : IEntityTypeConfiguration<ComplexTranslation>
{
    public void Configure(EntityTypeBuilder<ComplexTranslation> builder)
    {
        builder.ToTable("complex_translations");
        builder.HasKey(t => new { t.ComplexId, t.Locale });
        builder.HasLocale();
        builder.Property(t => t.Name).HasMaxLength(200).UseCollation(CatalogDbContext.TurkishCollation);
        builder.HasTimestamps();
    }
}

internal class ComplexImageConfiguration : IEntityTypeConfiguration<ComplexImage>
{
    public void Configure(EntityTypeBuilder<ComplexImage> builder)
    {
        // 0 = cover
        builder.ToTable("complex_images", t => t.HasCheckConstraint("ck_complex_images_position", $"position BETWEEN 0 AND {ComplexImage.MaxCount - 1}"));

        builder.HasKey(i => new { i.ComplexId, i.Position });
        builder.HasOne(i => i.Media).WithMany().HasForeignKey(i => i.MediaId).OnDelete(DeleteBehavior.NoAction);
        builder.HasIndex(i => i.MediaId).HasDatabaseName("ix_complex_images_media");
    }
}
