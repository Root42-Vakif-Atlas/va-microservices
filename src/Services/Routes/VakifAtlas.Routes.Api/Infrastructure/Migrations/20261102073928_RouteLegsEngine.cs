using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VakifAtlas.Routes.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RouteLegsEngine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "legs_engine",
                table: "routes",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "legs_engine",
                table: "routes");
        }
    }
}
