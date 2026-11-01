using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VakifAtlas.Catalog.Domain.References;

namespace VakifAtlas.Catalog.Api.Infrastructure.Persistence.Configurations;

internal static class ConfigurationExtensions
{
    // created_at / updated_at default to now(); updated_at is bumped by the set_updated_at trigger
    public static void HasTimestamps<T>(this EntityTypeBuilder<T> builder) where T : class
    {
        builder.Property<DateTimeOffset>("CreatedAt").HasDefaultValueSql("now()");
        builder.Property<DateTimeOffset>("UpdatedAt").HasDefaultValueSql("now()").ValueGeneratedOnAddOrUpdate();
    }

    // DB default true; sentinel true so an explicit false is still sent on insert
    public static PropertyBuilder<bool> HasActiveDefault(this PropertyBuilder<bool> property) =>
        property.HasDefaultValue(true).HasSentinel(true);

    public static void HasLocale<T>(this EntityTypeBuilder<T> builder) where T : class
    {
        builder.Property<string>("Locale").HasMaxLength(5);
        builder.HasOne<Locale>().WithMany().HasForeignKey("Locale").OnDelete(DeleteBehavior.NoAction);
    }

    // latitude/longitude pair; mercator_x/y are generated (catalog_mercator_y, migration 0003) and range-indexed
    // for tile queries
    public static void HasLocation<T>(this EntityTypeBuilder<T> builder, string table) where T : class
    {
        builder.Ignore("Location");
        builder.Property<double?>("MercatorX").HasComputedColumnSql("(longitude + 180) / 360", stored: true);
        builder.Property<double?>("MercatorY").HasComputedColumnSql("catalog_mercator_y(latitude)", stored: true);
        builder.HasIndex("MercatorX", "MercatorY").HasDatabaseName($"ix_{table}_mercator").HasFilter("mercator_x IS NOT NULL");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint($"ck_{table}_location_pair", "(latitude IS NULL) = (longitude IS NULL)");
            t.HasCheckConstraint($"ck_{table}_location_range", "latitude BETWEEN -90 AND 90 AND longitude BETWEEN -180 AND 180");
        });
    }

    // bumped by the bump_revision trigger on every UPDATE
    public static void HasRevision<T>(this EntityTypeBuilder<T> builder) where T : class =>
        builder.Property<int>("Revision").HasDefaultValue(0).ValueGeneratedOnAddOrUpdate();

    public static string InList(IEnumerable<string> values) => string.Join(", ", values.Select(v => $"'{v}'"));
}
