using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VakifAtlas.Feedback.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class StaffTriage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "handled_at",
                table: "feedback",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "handled_by",
                table: "feedback",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "handled_by_name",
                table: "feedback",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "staff_note",
                table: "feedback",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "status",
                table: "feedback",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "new");

            migrationBuilder.CreateIndex(
                name: "ix_feedback_status_created_at",
                table: "feedback",
                columns: new[] { "status", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_feedback_status_created_at",
                table: "feedback");

            migrationBuilder.DropColumn(
                name: "handled_at",
                table: "feedback");

            migrationBuilder.DropColumn(
                name: "handled_by",
                table: "feedback");

            migrationBuilder.DropColumn(
                name: "handled_by_name",
                table: "feedback");

            migrationBuilder.DropColumn(
                name: "staff_note",
                table: "feedback");

            migrationBuilder.DropColumn(
                name: "status",
                table: "feedback");
        }
    }
}
