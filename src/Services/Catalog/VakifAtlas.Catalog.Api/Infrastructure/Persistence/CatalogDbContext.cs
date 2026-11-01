using Microsoft.EntityFrameworkCore;
using VakifAtlas.Catalog.Domain;
using VakifAtlas.Catalog.Domain.Complexes;
using VakifAtlas.Catalog.Domain.References;
using VakifAtlas.Catalog.Domain.Works;
using VakifAtlas.Media;
using VakifAtlas.Messaging;

namespace VakifAtlas.Catalog.Api.Infrastructure.Persistence;

public class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    // ICU Turkish ordering (Ç, Ğ, I/İ, Ö, Ş, Ü) of titles and names
    public const string TurkishCollation = "tr_icu";

    public DbSet<City> Cities => Set<City>();
    public DbSet<Locale> Locales => Set<Locale>();
    public DbSet<MediaAsset> MediaAssets => Set<MediaAsset>();
    public DbSet<Complex> Complexes => Set<Complex>();
    public DbSet<Work> Works => Set<Work>();
    public DbSet<WorkPart> WorkParts => Set<WorkPart>();
    public DbSet<CatalogMeta> Meta => Set<CatalogMeta>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.UseIdentityAlwaysColumns();
        modelBuilder.HasCollation(TurkishCollation, locale: "tr-TR", provider: "icu", deterministic: true);
        modelBuilder.AddMessagingTables();
        modelBuilder.AddPhotoUploads();
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CatalogDbContext).Assembly);
    }
}
