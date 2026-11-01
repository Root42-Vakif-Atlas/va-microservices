using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VakifAtlas.Memories.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class StaffModeration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "resolution",
                table: "memory_reports",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "open");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "resolved_at",
                table: "memory_reports",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "moderated_at",
                table: "memories",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "moderated_by",
                table: "memories",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "moderated_by_name",
                table: "memories",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "moderation_note",
                table: "memories",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "moderation_reason",
                table: "memories",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "published_at",
                table: "memories",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_memories_report_count",
                table: "memories",
                column: "report_count",
                filter: "report_count > 0");

            migrationBuilder.CreateIndex(
                name: "ix_memories_status_created_at",
                table: "memories",
                columns: new[] { "status", "created_at" });

            // Mevcut yayındaki anılar zaten MemoryPublished olayını göndermişti (onay ikinci kez ödüllendirilmez).
            migrationBuilder.Sql("UPDATE memories SET published_at = created_at WHERE status = 'approved';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_memories_report_count",
                table: "memories");

            migrationBuilder.DropIndex(
                name: "ix_memories_status_created_at",
                table: "memories");

            migrationBuilder.DropColumn(
                name: "resolution",
                table: "memory_reports");

            migrationBuilder.DropColumn(
                name: "resolved_at",
                table: "memory_reports");

            migrationBuilder.DropColumn(
                name: "moderated_at",
                table: "memories");

            migrationBuilder.DropColumn(
                name: "moderated_by",
                table: "memories");

            migrationBuilder.DropColumn(
                name: "moderated_by_name",
                table: "memories");

            migrationBuilder.DropColumn(
                name: "moderation_note",
                table: "memories");

            migrationBuilder.DropColumn(
                name: "moderation_reason",
                table: "memories");

            migrationBuilder.DropColumn(
                name: "published_at",
                table: "memories");
        }
    }
}
