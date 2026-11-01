using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VakifAtlas.Catalog.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SeedReferenceData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // raw SQL instead of HasData: HasData omits is_active = false, so the DB default (true) would win
            migrationBuilder.Sql("INSERT INTO locales (code) VALUES ('tr'), ('en');");

            migrationBuilder.Sql("""
                INSERT INTO cities (code, name, is_active) VALUES
                    (16, 'Bursa',   true),
                    (31, 'Hatay',   true),
                    (35, 'İzmir',   false),
                    (42, 'Konya',   true),
                    (43, 'Kütahya', false),   -- passive
                    (45, 'Manisa',  false);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM cities WHERE code IN (16, 31, 35, 42, 43, 45);");
            migrationBuilder.Sql("DELETE FROM locales WHERE code IN ('tr', 'en');");
        }
    }
}
