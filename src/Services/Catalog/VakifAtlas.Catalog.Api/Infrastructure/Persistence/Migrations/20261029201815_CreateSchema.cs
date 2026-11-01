using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace VakifAtlas.Catalog.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CreateSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // keeps updated_at current on every UPDATE (attached by triggers at the bottom)
            migrationBuilder.Sql("""
                CREATE FUNCTION set_updated_at() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    NEW.updated_at = now();
                    RETURN NEW;
                END $$;
                """);

            migrationBuilder.CreateTable(
                name: "cities",
                columns: table => new
                {
                    code = table.Column<short>(type: "smallint", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cities", x => x.code);
                });

            migrationBuilder.CreateTable(
                name: "locales",
                columns: table => new
                {
                    code = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_locales", x => x.code);
                });

            migrationBuilder.CreateTable(
                name: "media_assets",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    storage_key = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    source_path = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    mime_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    size_bytes = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_media_assets", x => x.id);
                    table.CheckConstraint("ck_media_assets_size_bytes", "size_bytes <= 5242880");
                });

            migrationBuilder.CreateTable(
                name: "complexes",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    city_code = table.Column<short>(type: "smallint", nullable: false),
                    district = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_complexes", x => x.id);
                    table.UniqueConstraint("uq_complexes_id_city", x => new { x.id, x.city_code });
                    table.ForeignKey(
                        name: "fk_complexes_cities_city_code",
                        column: x => x.city_code,
                        principalTable: "cities",
                        principalColumn: "code");
                });

            migrationBuilder.CreateTable(
                name: "complex_images",
                columns: table => new
                {
                    complex_id = table.Column<long>(type: "bigint", nullable: false),
                    position = table.Column<short>(type: "smallint", nullable: false),
                    media_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_complex_images", x => new { x.complex_id, x.position });
                    table.CheckConstraint("ck_complex_images_position", "position BETWEEN 0 AND 19");
                    table.ForeignKey(
                        name: "fk_complex_images_complexes_complex_id",
                        column: x => x.complex_id,
                        principalTable: "complexes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_complex_images_media_assets_media_id",
                        column: x => x.media_id,
                        principalTable: "media_assets",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "complex_translations",
                columns: table => new
                {
                    complex_id = table.Column<long>(type: "bigint", nullable: false),
                    locale = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_complex_translations", x => new { x.complex_id, x.locale });
                    table.ForeignKey(
                        name: "fk_complex_translations_complexes_complex_id",
                        column: x => x.complex_id,
                        principalTable: "complexes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_complex_translations_locales_locale",
                        column: x => x.locale,
                        principalTable: "locales",
                        principalColumn: "code");
                });

            migrationBuilder.CreateTable(
                name: "works",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    city_code = table.Column<short>(type: "smallint", nullable: false),
                    complex_id = table.Column<long>(type: "bigint", nullable: true),
                    complex_position = table.Column<int>(type: "integer", nullable: true),
                    address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_works", x => x.id);
                    table.CheckConstraint("ck_works_complex_pair", "(complex_id IS NULL) = (complex_position IS NULL)");
                    table.ForeignKey(
                        name: "fk_works_cities_city_code",
                        column: x => x.city_code,
                        principalTable: "cities",
                        principalColumn: "code");
                    table.ForeignKey(
                        name: "fk_works_complex_city",
                        columns: x => new { x.complex_id, x.city_code },
                        principalTable: "complexes",
                        principalColumns: new[] { "id", "city_code" });
                });

            migrationBuilder.CreateTable(
                name: "work_images",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    work_id = table.Column<long>(type: "bigint", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    media_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_work_images", x => x.id);
                    table.ForeignKey(
                        name: "fk_work_images_media_assets_media_id",
                        column: x => x.media_id,
                        principalTable: "media_assets",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_work_images_works_work_id",
                        column: x => x.work_id,
                        principalTable: "works",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "work_parts",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    work_id = table.Column<long>(type: "bigint", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_work_parts", x => x.id);
                    table.ForeignKey(
                        name: "fk_work_parts_works_work_id",
                        column: x => x.work_id,
                        principalTable: "works",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "work_translations",
                columns: table => new
                {
                    work_id = table.Column<long>(type: "bigint", nullable: false),
                    locale = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    slug = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    nickname = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    short_description_title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    short_description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_work_translations", x => new { x.work_id, x.locale });
                    table.CheckConstraint("ck_work_translations_tr_no_slug", "locale <> 'tr' OR slug IS NULL");
                    table.ForeignKey(
                        name: "fk_work_translations_locales_locale",
                        column: x => x.locale,
                        principalTable: "locales",
                        principalColumn: "code");
                    table.ForeignKey(
                        name: "fk_work_translations_works_work_id",
                        column: x => x.work_id,
                        principalTable: "works",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "work_image_translations",
                columns: table => new
                {
                    work_image_id = table.Column<long>(type: "bigint", nullable: false),
                    locale = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    alt_text = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_work_image_translations", x => new { x.work_image_id, x.locale });
                    table.ForeignKey(
                        name: "fk_work_image_translations_locales_locale",
                        column: x => x.locale,
                        principalTable: "locales",
                        principalColumn: "code");
                    table.ForeignKey(
                        name: "fk_work_image_translations_work_images_work_image_id",
                        column: x => x.work_image_id,
                        principalTable: "work_images",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "work_part_images",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    part_id = table.Column<long>(type: "bigint", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    media_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_work_part_images", x => x.id);
                    table.ForeignKey(
                        name: "fk_work_part_images_media_assets_media_id",
                        column: x => x.media_id,
                        principalTable: "media_assets",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_work_part_images_work_parts_part_id",
                        column: x => x.part_id,
                        principalTable: "work_parts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "work_part_translations",
                columns: table => new
                {
                    part_id = table.Column<long>(type: "bigint", nullable: false),
                    locale = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    description = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_work_part_translations", x => new { x.part_id, x.locale });
                    table.ForeignKey(
                        name: "fk_work_part_translations_locales_locale",
                        column: x => x.locale,
                        principalTable: "locales",
                        principalColumn: "code");
                    table.ForeignKey(
                        name: "fk_work_part_translations_work_parts_part_id",
                        column: x => x.part_id,
                        principalTable: "work_parts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "work_part_image_translations",
                columns: table => new
                {
                    part_image_id = table.Column<long>(type: "bigint", nullable: false),
                    locale = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    alt_text = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_work_part_image_translations", x => new { x.part_image_id, x.locale });
                    table.ForeignKey(
                        name: "fk_work_part_image_translations_locales_locale",
                        column: x => x.locale,
                        principalTable: "locales",
                        principalColumn: "code");
                    table.ForeignKey(
                        name: "fk_work_part_image_translations_work_part_images_part_image_id",
                        column: x => x.part_image_id,
                        principalTable: "work_part_images",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_complex_images_media",
                table: "complex_images",
                column: "media_id");

            migrationBuilder.CreateIndex(
                name: "ix_complex_translations_locale",
                table: "complex_translations",
                column: "locale");

            migrationBuilder.CreateIndex(
                name: "ix_complexes_city",
                table: "complexes",
                column: "city_code");

            migrationBuilder.CreateIndex(
                name: "ix_complexes_key",
                table: "complexes",
                column: "key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_media_assets_storage_key",
                table: "media_assets",
                column: "storage_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_work_image_translations_locale",
                table: "work_image_translations",
                column: "locale");

            migrationBuilder.CreateIndex(
                name: "ix_work_images_media",
                table: "work_images",
                column: "media_id");

            migrationBuilder.CreateIndex(
                name: "ix_work_images_work_id_position",
                table: "work_images",
                columns: new[] { "work_id", "position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_work_part_image_translations_locale",
                table: "work_part_image_translations",
                column: "locale");

            migrationBuilder.CreateIndex(
                name: "ix_work_part_images_media",
                table: "work_part_images",
                column: "media_id");

            migrationBuilder.CreateIndex(
                name: "ix_work_part_images_part_id_position",
                table: "work_part_images",
                columns: new[] { "part_id", "position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_work_part_translations_locale",
                table: "work_part_translations",
                column: "locale");

            migrationBuilder.CreateIndex(
                name: "ix_work_parts_work_id_position",
                table: "work_parts",
                columns: new[] { "work_id", "position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_work_translations_locale_slug",
                table: "work_translations",
                columns: new[] { "locale", "slug" },
                unique: true,
                filter: "slug IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_works_city",
                table: "works",
                column: "city_code");

            migrationBuilder.CreateIndex(
                name: "ix_works_complex_id_city_code",
                table: "works",
                columns: new[] { "complex_id", "city_code" });

            migrationBuilder.CreateIndex(
                name: "ix_works_key",
                table: "works",
                column: "key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_works_complex_position",
                table: "works",
                columns: new[] { "complex_id", "complex_position" },
                unique: true);

            foreach (var table in new[] { "complexes", "complex_translations", "works", "work_translations" })
            {
                migrationBuilder.Sql($"CREATE TRIGGER trg_{table}_updated_at BEFORE UPDATE ON {table} FOR EACH ROW EXECUTE FUNCTION set_updated_at();");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "complex_images");

            migrationBuilder.DropTable(
                name: "complex_translations");

            migrationBuilder.DropTable(
                name: "work_image_translations");

            migrationBuilder.DropTable(
                name: "work_part_image_translations");

            migrationBuilder.DropTable(
                name: "work_part_translations");

            migrationBuilder.DropTable(
                name: "work_translations");

            migrationBuilder.DropTable(
                name: "work_images");

            migrationBuilder.DropTable(
                name: "work_part_images");

            migrationBuilder.DropTable(
                name: "locales");

            migrationBuilder.DropTable(
                name: "media_assets");

            migrationBuilder.DropTable(
                name: "work_parts");

            migrationBuilder.DropTable(
                name: "works");

            migrationBuilder.DropTable(
                name: "complexes");

            migrationBuilder.DropTable(
                name: "cities");

            migrationBuilder.Sql("DROP FUNCTION set_updated_at();");
        }
    }
}
