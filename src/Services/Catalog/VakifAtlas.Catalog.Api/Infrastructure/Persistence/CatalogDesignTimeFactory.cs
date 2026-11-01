using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace VakifAtlas.Catalog.Api.Infrastructure.Persistence;

// EF tooling (dotnet ef migrations) builds the context without starting the app; same provider setup as
// AddPostgresDbContext. No connection is opened while adding migrations
internal sealed class CatalogDesignTimeFactory : IDesignTimeDbContextFactory<CatalogDbContext>
{
    public CatalogDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<CatalogDbContext>()
        .UseNpgsql(Environment.GetEnvironmentVariable("ConnectionStrings__Catalog") ?? "Host=localhost;Database=vakifatlas_catalog")
        .UseSnakeCaseNamingConvention()
        .Options);
}
