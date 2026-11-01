using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VakifAtlas.Catalog.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CatalogFeatures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // same statements as migrations/0003_catalog_features.sql; functions first, the generated columns use them
            migrationBuilder.Sql("""
                CREATE FUNCTION catalog_fold(value text) RETURNS text
                    LANGUAGE sql IMMUTABLE PARALLEL SAFE RETURNS NULL ON NULL INPUT
                    AS $$ SELECT lower(translate(value, 'ÇĞİIÖŞÜÂÎÛçğıöşüâîû', 'CGIIOSUAIUcgiosuaiu')) $$;

                CREATE FUNCTION catalog_mercator_y(latitude double precision) RETURNS double precision
                    LANGUAGE sql IMMUTABLE PARALLEL SAFE RETURNS NULL ON NULL INPUT
                    AS $$
                        SELECT least(1, greatest(0, 0.5 - ln((1 + s) / (1 - s)) / (4 * pi())))
                        FROM (SELECT sin(radians(least(85.05112878, greatest(-85.05112878, latitude)))) AS s) projected
                    $$;

                CREATE FUNCTION bump_revision() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    NEW.revision = OLD.revision + 1;
                    RETURN NEW;
                END $$;
                """);

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:CollationDefinition:tr_icu", "tr-TR,tr-TR,icu,True");

            migrationBuilder.AddColumn<string>(
                name: "category",
                table: "works",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "other");

            migrationBuilder.AddColumn<bool>(
                name: "category_overridden",
                table: "works",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "district",
                table: "works",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_unesco_world_heritage",
                table: "works",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<double>(
                name: "latitude",
                table: "works",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "longitude",
                table: "works",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "period",
                table: "works",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "other");

            migrationBuilder.AddColumn<int>(
                name: "revision",
                table: "works",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AlterColumn<string>(
                name: "title",
                table: "work_translations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true,
                collation: "tr_icu",
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200,
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "architect",
                table: "work_translations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "construction_date",
                table: "work_translations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "founder",
                table: "work_translations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "history",
                table: "work_translations",
                type: "text",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "storage_key",
                table: "media_assets",
                type: "character varying(400)",
                maxLength: 400,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(400)",
                oldMaxLength: 400);

            migrationBuilder.AlterColumn<int>(
                name: "size_bytes",
                table: "media_assets",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<string>(
                name: "mime_type",
                table: "media_assets",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100);

            migrationBuilder.AddColumn<string>(
                name: "author",
                table: "media_assets",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "license",
                table: "media_assets",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "mirror_attempts",
                table: "media_assets",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.AddColumn<string>(
                name: "mirror_error",
                table: "media_assets",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "mirror_retry_at",
                table: "media_assets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "origin_url",
                table: "media_assets",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source_name",
                table: "media_assets",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source_url",
                table: "media_assets",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "thumbnail_key",
                table: "media_assets",
                type: "character varying(400)",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "latitude",
                table: "complexes",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "longitude",
                table: "complexes",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "revision",
                table: "complexes",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AlterColumn<string>(
                name: "name",
                table: "complex_translations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                collation: "tr_icu",
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200);

            migrationBuilder.AddColumn<double>(
                name: "mercator_x",
                table: "works",
                type: "double precision",
                nullable: true,
                computedColumnSql: "(longitude + 180) / 360",
                stored: true);

            migrationBuilder.AddColumn<double>(
                name: "mercator_y",
                table: "works",
                type: "double precision",
                nullable: true,
                computedColumnSql: "catalog_mercator_y(latitude)",
                stored: true);

            migrationBuilder.AddColumn<double>(
                name: "mercator_x",
                table: "complexes",
                type: "double precision",
                nullable: true,
                computedColumnSql: "(longitude + 180) / 360",
                stored: true);

            migrationBuilder.AddColumn<double>(
                name: "mercator_y",
                table: "complexes",
                type: "double precision",
                nullable: true,
                computedColumnSql: "catalog_mercator_y(latitude)",
                stored: true);

            migrationBuilder.CreateTable(
                name: "catalog_meta",
                columns: table => new
                {
                    id = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)1),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_catalog_meta", x => x.id);
                    table.CheckConstraint("ck_catalog_meta_singleton", "id = 1");
                });

            migrationBuilder.CreateTable(
                name: "inbox_messages",
                columns: table => new
                {
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    consumer = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inbox_messages", x => new { x.message_id, x.consumer });
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    trace_parent = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "photo_uploads",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    owner_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    byte_size = table.Column<long>(type: "bigint", nullable: false),
                    width = table.Column<int>(type: "integer", nullable: false),
                    height = table.Column<int>(type: "integer", nullable: false),
                    object_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    consumed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_photo_uploads", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_works_mercator",
                table: "works",
                columns: new[] { "mercator_x", "mercator_y" },
                filter: "mercator_x IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_works_updated_at_id",
                table: "works",
                columns: new[] { "updated_at", "id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_works_category",
                table: "works",
                sql: "category IN ('mosque', 'madrasa', 'inn', 'caravanserai', 'bath', 'fountain', 'tomb', 'hospital', 'imaret', 'other')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_works_complex_position",
                table: "works",
                sql: "complex_position >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_works_location_pair",
                table: "works",
                sql: "(latitude IS NULL) = (longitude IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_works_location_range",
                table: "works",
                sql: "latitude BETWEEN -90 AND 90 AND longitude BETWEEN -180 AND 180");

            migrationBuilder.AddCheckConstraint(
                name: "ck_works_period",
                table: "works",
                sql: "period IN ('seljuk', 'beylik', 'ottoman', 'other')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_work_parts_position",
                table: "work_parts",
                sql: "position >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_work_part_images_position",
                table: "work_part_images",
                sql: "position >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_work_images_position",
                table: "work_images",
                sql: "position >= 0");

            migrationBuilder.CreateIndex(
                name: "ix_media_assets_mirror_pending",
                table: "media_assets",
                column: "mirror_retry_at",
                filter: "storage_key IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_media_assets_origin_url",
                table: "media_assets",
                column: "origin_url",
                unique: true,
                filter: "origin_url IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_media_assets_source",
                table: "media_assets",
                sql: "storage_key IS NOT NULL OR origin_url IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_media_assets_stored",
                table: "media_assets",
                sql: "(storage_key IS NULL) = (mime_type IS NULL) AND (storage_key IS NULL) = (size_bytes IS NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_complexes_mercator",
                table: "complexes",
                columns: new[] { "mercator_x", "mercator_y" },
                filter: "mercator_x IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_complexes_location_pair",
                table: "complexes",
                sql: "(latitude IS NULL) = (longitude IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_complexes_location_range",
                table: "complexes",
                sql: "latitude BETWEEN -90 AND 90 AND longitude BETWEEN -180 AND 180");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_pending",
                table: "outbox_messages",
                column: "occurred_at",
                filter: "processed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_photo_uploads_owner_id",
                table: "photo_uploads",
                column: "owner_id");

            migrationBuilder.CreateIndex(
                name: "ix_photo_uploads_pending",
                table: "photo_uploads",
                column: "expires_at",
                filter: "consumed_at IS NULL");

            migrationBuilder.Sql("INSERT INTO catalog_meta (id) VALUES (1);");

            // importer objects so far kept the thumbnail next to the image
            migrationBuilder.Sql("""
                UPDATE media_assets SET thumbnail_key = regexp_replace(storage_key, '\.[^./]+$', '') || '-thumb.jpg'
                WHERE storage_key LIKE 'catalog/\_import/%';
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER trg_works_revision     BEFORE UPDATE ON works     FOR EACH ROW EXECUTE FUNCTION bump_revision();
                CREATE TRIGGER trg_complexes_revision BEFORE UPDATE ON complexes FOR EACH ROW EXECUTE FUNCTION bump_revision();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER trg_works_revision ON works;
                DROP TRIGGER trg_complexes_revision ON complexes;
                """);

            migrationBuilder.DropTable(
                name: "catalog_meta");

            migrationBuilder.DropTable(
                name: "inbox_messages");

            migrationBuilder.DropTable(
                name: "outbox_messages");

            migrationBuilder.DropTable(
                name: "photo_uploads");

            migrationBuilder.DropIndex(
                name: "ix_works_mercator",
                table: "works");

            migrationBuilder.DropIndex(
                name: "ix_works_updated_at_id",
                table: "works");

            migrationBuilder.DropCheckConstraint(
                name: "ck_works_category",
                table: "works");

            migrationBuilder.DropCheckConstraint(
                name: "ck_works_complex_position",
                table: "works");

            migrationBuilder.DropCheckConstraint(
                name: "ck_works_location_pair",
                table: "works");

            migrationBuilder.DropCheckConstraint(
                name: "ck_works_location_range",
                table: "works");

            migrationBuilder.DropCheckConstraint(
                name: "ck_works_period",
                table: "works");

            migrationBuilder.DropCheckConstraint(
                name: "ck_work_parts_position",
                table: "work_parts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_work_part_images_position",
                table: "work_part_images");

            migrationBuilder.DropCheckConstraint(
                name: "ck_work_images_position",
                table: "work_images");

            migrationBuilder.DropIndex(
                name: "ix_media_assets_mirror_pending",
                table: "media_assets");

            migrationBuilder.DropIndex(
                name: "ux_media_assets_origin_url",
                table: "media_assets");

            migrationBuilder.DropCheckConstraint(
                name: "ck_media_assets_source",
                table: "media_assets");

            migrationBuilder.DropCheckConstraint(
                name: "ck_media_assets_stored",
                table: "media_assets");

            migrationBuilder.DropIndex(
                name: "ix_complexes_mercator",
                table: "complexes");

            migrationBuilder.DropCheckConstraint(
                name: "ck_complexes_location_pair",
                table: "complexes");

            migrationBuilder.DropCheckConstraint(
                name: "ck_complexes_location_range",
                table: "complexes");

            migrationBuilder.DropColumn(
                name: "mercator_x",
                table: "works");

            migrationBuilder.DropColumn(
                name: "mercator_y",
                table: "works");

            migrationBuilder.DropColumn(
                name: "mercator_x",
                table: "complexes");

            migrationBuilder.DropColumn(
                name: "mercator_y",
                table: "complexes");

            migrationBuilder.DropColumn(
                name: "category",
                table: "works");

            migrationBuilder.DropColumn(
                name: "category_overridden",
                table: "works");

            migrationBuilder.DropColumn(
                name: "district",
                table: "works");

            migrationBuilder.DropColumn(
                name: "is_unesco_world_heritage",
                table: "works");

            migrationBuilder.DropColumn(
                name: "latitude",
                table: "works");

            migrationBuilder.DropColumn(
                name: "longitude",
                table: "works");

            migrationBuilder.DropColumn(
                name: "period",
                table: "works");

            migrationBuilder.DropColumn(
                name: "revision",
                table: "works");

            migrationBuilder.DropColumn(
                name: "architect",
                table: "work_translations");

            migrationBuilder.DropColumn(
                name: "construction_date",
                table: "work_translations");

            migrationBuilder.DropColumn(
                name: "founder",
                table: "work_translations");

            migrationBuilder.DropColumn(
                name: "history",
                table: "work_translations");

            migrationBuilder.DropColumn(
                name: "author",
                table: "media_assets");

            migrationBuilder.DropColumn(
                name: "license",
                table: "media_assets");

            migrationBuilder.DropColumn(
                name: "mirror_attempts",
                table: "media_assets");

            migrationBuilder.DropColumn(
                name: "mirror_error",
                table: "media_assets");

            migrationBuilder.DropColumn(
                name: "mirror_retry_at",
                table: "media_assets");

            migrationBuilder.DropColumn(
                name: "origin_url",
                table: "media_assets");

            migrationBuilder.DropColumn(
                name: "source_name",
                table: "media_assets");

            migrationBuilder.DropColumn(
                name: "source_url",
                table: "media_assets");

            migrationBuilder.DropColumn(
                name: "thumbnail_key",
                table: "media_assets");

            migrationBuilder.DropColumn(
                name: "latitude",
                table: "complexes");

            migrationBuilder.DropColumn(
                name: "longitude",
                table: "complexes");

            migrationBuilder.DropColumn(
                name: "revision",
                table: "complexes");

            migrationBuilder.AlterColumn<string>(
                name: "title",
                table: "work_translations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200,
                oldNullable: true,
                oldCollation: "tr_icu");

            migrationBuilder.AlterColumn<string>(
                name: "storage_key",
                table: "media_assets",
                type: "character varying(400)",
                maxLength: 400,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(400)",
                oldMaxLength: 400,
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "size_bytes",
                table: "media_assets",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "mime_type",
                table: "media_assets",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "name",
                table: "complex_translations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200,
                oldCollation: "tr_icu");

            // after the columns that use it
            migrationBuilder.AlterDatabase()
                .OldAnnotation("Npgsql:CollationDefinition:tr_icu", "tr-TR,tr-TR,icu,True");

            // images added by URL and not mirrored yet have no storage_key: rolling back fails on them (NOT NULL) by design
            migrationBuilder.Sql("""
                DROP FUNCTION bump_revision();
                DROP FUNCTION catalog_mercator_y(double precision);
                DROP FUNCTION catalog_fold(text);
                """);
        }
    }
}
