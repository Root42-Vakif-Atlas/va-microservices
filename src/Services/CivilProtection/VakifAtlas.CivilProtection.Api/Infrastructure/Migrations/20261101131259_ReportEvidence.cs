using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VakifAtlas.CivilProtection.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ReportEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "evidence_level",
                table: "damage_reports",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "evidence_reasons",
                table: "damage_reports",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "evidence_scored_at",
                table: "damage_reports",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "evidence_signals",
                table: "damage_reports",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "evidence_version",
                table: "damage_reports",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_damage_reports_created_at",
                table: "damage_reports",
                column: "created_at",
                filter: "evidence_scored_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_damage_reports_evidence_level_created_at",
                table: "damage_reports",
                columns: new[] { "evidence_level", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_damage_reports_created_at",
                table: "damage_reports");

            migrationBuilder.DropIndex(
                name: "ix_damage_reports_evidence_level_created_at",
                table: "damage_reports");

            migrationBuilder.DropColumn(
                name: "evidence_level",
                table: "damage_reports");

            migrationBuilder.DropColumn(
                name: "evidence_reasons",
                table: "damage_reports");

            migrationBuilder.DropColumn(
                name: "evidence_scored_at",
                table: "damage_reports");

            migrationBuilder.DropColumn(
                name: "evidence_signals",
                table: "damage_reports");

            migrationBuilder.DropColumn(
                name: "evidence_version",
                table: "damage_reports");
        }
    }
}
