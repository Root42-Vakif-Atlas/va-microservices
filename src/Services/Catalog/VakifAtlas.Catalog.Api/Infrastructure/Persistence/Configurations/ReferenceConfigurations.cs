using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VakifAtlas.Catalog.Domain;
using VakifAtlas.Catalog.Domain.References;

namespace VakifAtlas.Catalog.Api.Infrastructure.Persistence.Configurations;

internal class CityConfiguration : IEntityTypeConfiguration<City>
{
    public void Configure(EntityTypeBuilder<City> builder)
    {
        builder.HasKey(c => c.Code);
        builder.Property(c => c.Code).ValueGeneratedNever();
        builder.Property(c => c.Name).HasMaxLength(100);
        builder.Property(c => c.IsActive).HasActiveDefault();
    }
}

internal class LocaleConfiguration : IEntityTypeConfiguration<Locale>
{
    public void Configure(EntityTypeBuilder<Locale> builder)
    {
        builder.HasKey(l => l.Code);
        builder.Property(l => l.Code).HasMaxLength(5);
    }
}

internal class MediaAssetConfiguration : IEntityTypeConfiguration<MediaAsset>
{
    public void Configure(EntityTypeBuilder<MediaAsset> builder)
    {
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_media_assets_size_bytes", $"size_bytes <= {MediaAsset.MaxSizeBytes}");
            t.HasCheckConstraint("ck_media_assets_source", "storage_key IS NOT NULL OR origin_url IS NOT NULL");
            t.HasCheckConstraint("ck_media_assets_stored",
                "(storage_key IS NULL) = (mime_type IS NULL) AND (storage_key IS NULL) = (size_bytes IS NULL)");
        });

        builder.Property(m => m.StorageKey).HasMaxLength(400);
        builder.HasIndex(m => m.StorageKey).IsUnique();
        builder.Property(m => m.ThumbnailKey).HasMaxLength(400);
        builder.Property(m => m.SourcePath).HasMaxLength(300);
        builder.Property(m => m.MimeType).HasMaxLength(100);
        builder.Property(m => m.Author).HasMaxLength(200);
        builder.Property(m => m.License).HasMaxLength(200);
        builder.Property(m => m.SourceName).HasMaxLength(200);
        builder.Property(m => m.SourceUrl).HasMaxLength(2000);
        builder.Property(m => m.OriginUrl).HasMaxLength(2000);
        builder.HasIndex(m => m.OriginUrl).IsUnique().HasFilter("origin_url IS NOT NULL").HasDatabaseName("ux_media_assets_origin_url");
        builder.Property(m => m.MirrorAttempts).HasDefaultValue((short)0);
        builder.Property(m => m.MirrorError).HasMaxLength(500);
        builder.HasIndex(m => m.MirrorRetryAt).HasFilter("storage_key IS NULL").HasDatabaseName("ix_media_assets_mirror_pending");
        builder.Ignore(m => m.IsStored);
        builder.Property(m => m.CreatedAt).HasDefaultValueSql("now()");
    }
}

internal class CatalogMetaConfiguration : IEntityTypeConfiguration<CatalogMeta>
{
    public void Configure(EntityTypeBuilder<CatalogMeta> builder)
    {
        builder.ToTable("catalog_meta", t => t.HasCheckConstraint("ck_catalog_meta_singleton", $"id = {CatalogMeta.SingletonId}"));
        builder.Property(m => m.Id).ValueGeneratedNever().HasDefaultValue(CatalogMeta.SingletonId);
        builder.Property(m => m.Version).HasDefaultValue(1L);
        builder.Property(m => m.UpdatedAt).HasDefaultValueSql("now()");
    }
}
