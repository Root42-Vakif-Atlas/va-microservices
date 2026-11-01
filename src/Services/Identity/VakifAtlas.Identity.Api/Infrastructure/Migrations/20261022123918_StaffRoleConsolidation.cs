using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VakifAtlas.Identity.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class StaffRoleConsolidation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Beş ayrıntılı personel rolü üçe indi: katalog → editor; anı, sivil koruma ve geri
            // bildirim (kullanıcı içeriği) → moderator. Yönetici (vgm_admin) değişmez.
            migrationBuilder.Sql("""
                UPDATE users SET roles = ARRAY(
                    SELECT DISTINCT CASE r
                        WHEN 'catalog_editor' THEN 'editor'
                        WHEN 'memory_moderator' THEN 'moderator'
                        WHEN 'civil_protection_reviewer' THEN 'moderator'
                        WHEN 'feedback_viewer' THEN 'moderator'
                        ELSE r END
                    FROM unnest(roles) AS r)
                WHERE roles && ARRAY['catalog_editor', 'memory_moderator', 'civil_protection_reviewer', 'feedback_viewer'];
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Geri dönüşte moderatör, kapsadığı üç ayrıntılı rolün hepsini alır.
            migrationBuilder.Sql("""
                UPDATE users SET roles = ARRAY(
                    SELECT DISTINCT x FROM unnest(roles) AS r,
                    unnest(CASE r
                        WHEN 'editor' THEN ARRAY['catalog_editor']
                        WHEN 'moderator' THEN ARRAY['memory_moderator', 'civil_protection_reviewer', 'feedback_viewer']
                        ELSE ARRAY[r] END) AS x)
                WHERE roles && ARRAY['editor', 'moderator'];
                """);
        }
    }
}
