using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VakifAtlas.Memories.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ImageModeration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ai_images",
                table: "memories",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ai_nsfw",
                table: "memories",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ai_similarity",
                table: "memories",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ai_vision_version",
                table: "memories",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ai_images",
                table: "memories");

            migrationBuilder.DropColumn(
                name: "ai_nsfw",
                table: "memories");

            migrationBuilder.DropColumn(
                name: "ai_similarity",
                table: "memories");

            migrationBuilder.DropColumn(
                name: "ai_vision_version",
                table: "memories");
        }
    }
}
