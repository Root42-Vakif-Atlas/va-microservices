using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VakifAtlas.Memories.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ContextModeration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ai_context_version",
                table: "memories",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ai_relevance",
                table: "memories",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ai_spam",
                table: "memories",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<List<string>>(
                name: "ai_spam_signals",
                table: "memories",
                type: "text[]",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ai_context_version",
                table: "memories");

            migrationBuilder.DropColumn(
                name: "ai_relevance",
                table: "memories");

            migrationBuilder.DropColumn(
                name: "ai_spam",
                table: "memories");

            migrationBuilder.DropColumn(
                name: "ai_spam_signals",
                table: "memories");
        }
    }
}
