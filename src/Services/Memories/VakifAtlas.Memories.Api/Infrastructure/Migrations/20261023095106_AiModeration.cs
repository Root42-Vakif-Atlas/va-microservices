using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VakifAtlas.Memories.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AiModeration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ai_latency_ms",
                table: "memories",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ai_lexicon_matched",
                table: "memories",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ai_model_version",
                table: "memories",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ai_review_cause",
                table: "memories",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ai_score",
                table: "memories",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ai_scored_at",
                table: "memories",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ai_scores",
                table: "memories",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ai_top_category",
                table: "memories",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ai_verdict",
                table: "memories",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_memories_ai_verdict_created_at",
                table: "memories",
                columns: new[] { "ai_verdict", "created_at" },
                filter: "ai_verdict IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_memories_status_ai_score",
                table: "memories",
                columns: new[] { "status", "ai_score" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_memories_ai_verdict_created_at",
                table: "memories");

            migrationBuilder.DropIndex(
                name: "ix_memories_status_ai_score",
                table: "memories");

            migrationBuilder.DropColumn(
                name: "ai_latency_ms",
                table: "memories");

            migrationBuilder.DropColumn(
                name: "ai_lexicon_matched",
                table: "memories");

            migrationBuilder.DropColumn(
                name: "ai_model_version",
                table: "memories");

            migrationBuilder.DropColumn(
                name: "ai_review_cause",
                table: "memories");

            migrationBuilder.DropColumn(
                name: "ai_score",
                table: "memories");

            migrationBuilder.DropColumn(
                name: "ai_scored_at",
                table: "memories");

            migrationBuilder.DropColumn(
                name: "ai_scores",
                table: "memories");

            migrationBuilder.DropColumn(
                name: "ai_top_category",
                table: "memories");

            migrationBuilder.DropColumn(
                name: "ai_verdict",
                table: "memories");
        }
    }
}
