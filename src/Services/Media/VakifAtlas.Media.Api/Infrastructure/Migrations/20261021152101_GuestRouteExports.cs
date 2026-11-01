using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VakifAtlas.Media.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class GuestRouteExports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The existing archive is kept: the owner column is renamed and may be empty for guest records.
            migrationBuilder.DropIndex(
                name: "ix_route_exports_owner_id_status_created_at_id",
                table: "route_exports");

            migrationBuilder.RenameColumn(
                name: "owner_id",
                table: "route_exports",
                newName: "user_id");

            migrationBuilder.AlterColumn<string>(
                name: "user_id",
                table: "route_exports",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(40)",
                oldMaxLength: 40);

            migrationBuilder.AddColumn<Guid>(
                name: "installation_id",
                table: "route_exports",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_route_exports_installation_id_status_created_at_id",
                table: "route_exports",
                columns: new[] { "installation_id", "status", "created_at", "id" },
                descending: new[] { false, false, true, true },
                filter: "user_id IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_route_exports_user_id_status_created_at_id",
                table: "route_exports",
                columns: new[] { "user_id", "status", "created_at", "id" },
                descending: new[] { false, false, true, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_route_exports_installation_id_status_created_at_id",
                table: "route_exports");

            migrationBuilder.DropIndex(
                name: "ix_route_exports_user_id_status_created_at_id",
                table: "route_exports");

            // Unowned (guest) records cannot be represented in the old schema.
            migrationBuilder.Sql("DELETE FROM route_exports WHERE user_id IS NULL;");

            migrationBuilder.DropColumn(
                name: "installation_id",
                table: "route_exports");

            migrationBuilder.AlterColumn<string>(
                name: "user_id",
                table: "route_exports",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(40)",
                oldMaxLength: 40,
                oldNullable: true);

            migrationBuilder.RenameColumn(
                name: "user_id",
                table: "route_exports",
                newName: "owner_id");

            migrationBuilder.CreateIndex(
                name: "ix_route_exports_owner_id_status_created_at_id",
                table: "route_exports",
                columns: new[] { "owner_id", "status", "created_at", "id" },
                descending: new[] { false, false, true, true });
        }
    }
}
