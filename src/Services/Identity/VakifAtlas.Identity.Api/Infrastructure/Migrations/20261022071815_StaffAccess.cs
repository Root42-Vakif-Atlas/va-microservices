using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VakifAtlas.Identity.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class StaffAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_staff_login_at",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "last_totp_step",
                table: "users",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "mfa_enrolled_at",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "mfa_secret",
                table: "users",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "staff_disabled_at",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "kind",
                table: "refresh_tokens",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Citizen");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "session_expires_at",
                table: "refresh_tokens",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "audit_log",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    service = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    action = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    target_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    target_id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    changes = table.Column<string>(type: "jsonb", nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "mfa_challenges",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    token_hash = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    purpose = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    pending_secret = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    consumed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mfa_challenges", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "recovery_codes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    code_hash = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_recovery_codes", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_users_roles",
                table: "users",
                column: "roles")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_actor_id_at",
                table: "audit_log",
                columns: new[] { "actor_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_at_id",
                table: "audit_log",
                columns: new[] { "at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_target_type_target_id",
                table: "audit_log",
                columns: new[] { "target_type", "target_id" });

            migrationBuilder.CreateIndex(
                name: "ix_mfa_challenges_expires_at",
                table: "mfa_challenges",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_mfa_challenges_token_hash",
                table: "mfa_challenges",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_recovery_codes_user_id_code_hash",
                table: "recovery_codes",
                columns: new[] { "user_id", "code_hash" },
                unique: true);

            // Tek personel rolü (vgm_staff) ayrıntılı rollere geçti: mevcut personel yönetici olur.
            migrationBuilder.Sql("UPDATE users SET roles = array_replace(roles, 'vgm_staff', 'vgm_admin') WHERE 'vgm_staff' = ANY(roles);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_log");

            migrationBuilder.DropTable(
                name: "mfa_challenges");

            migrationBuilder.DropTable(
                name: "recovery_codes");

            migrationBuilder.DropIndex(
                name: "ix_users_roles",
                table: "users");

            migrationBuilder.DropColumn(
                name: "last_staff_login_at",
                table: "users");

            migrationBuilder.DropColumn(
                name: "last_totp_step",
                table: "users");

            migrationBuilder.DropColumn(
                name: "mfa_enrolled_at",
                table: "users");

            migrationBuilder.DropColumn(
                name: "mfa_secret",
                table: "users");

            migrationBuilder.DropColumn(
                name: "staff_disabled_at",
                table: "users");

            migrationBuilder.DropColumn(
                name: "kind",
                table: "refresh_tokens");

            migrationBuilder.DropColumn(
                name: "session_expires_at",
                table: "refresh_tokens");
        }
    }
}
