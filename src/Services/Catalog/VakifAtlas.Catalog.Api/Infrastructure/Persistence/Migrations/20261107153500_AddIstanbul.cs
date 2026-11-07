using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VakifAtlas.Catalog.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIstanbul : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO cities (code, name, is_active) VALUES (34, 'İstanbul', true)
                ON CONFLICT (code) DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM cities WHERE code = 34;");
        }
    }
}
