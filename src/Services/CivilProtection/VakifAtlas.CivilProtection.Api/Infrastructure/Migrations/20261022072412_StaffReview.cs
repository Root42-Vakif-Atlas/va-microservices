using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VakifAtlas.CivilProtection.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class StaffReview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_damage_reports_monument_id",
                table: "damage_reports",
                column: "monument_id");

            migrationBuilder.CreateIndex(
                name: "ix_damage_reports_urgency_created_at",
                table: "damage_reports",
                columns: new[] { "urgency", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_damage_reports_monument_id",
                table: "damage_reports");

            migrationBuilder.DropIndex(
                name: "ix_damage_reports_urgency_created_at",
                table: "damage_reports");
        }
    }
}
