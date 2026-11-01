using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VakifAtlas.Routes.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SavedMonumentOwners : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "pk_saved_monuments",
                table: "saved_monuments");

            migrationBuilder.AddColumn<string>(
                name: "owner_key",
                table: "saved_monuments",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            // Mevcut kayıtlar kurulumun misafir kayıtlarıdır (OwnerScope.GuestKey: "i:" + Guid "N").
            migrationBuilder.Sql("UPDATE saved_monuments SET owner_key = 'i:' || replace(installation_id::text, '-', '');");

            migrationBuilder.AddPrimaryKey(
                name: "pk_saved_monuments",
                table: "saved_monuments",
                columns: new[] { "owner_key", "monument_id" });

            // Kurtarma: oturum açıkken rota oluşturulmuş bir kurulumun kaydedilenleri o hesaba taşınır
            // (bu sürümden önce kaydedilenler yalnızca kuruluma bağlıydı; uygulama verisi silinince
            // yeni kurulum kimliğiyle görünmez oluyordu).
            migrationBuilder.Sql("""
                INSERT INTO saved_monuments (owner_key, installation_id, monument_id, saved_at)
                SELECT 'u:' || r.user_id, s.installation_id, s.monument_id, s.saved_at
                FROM saved_monuments s
                JOIN (SELECT DISTINCT ON (installation_id) installation_id, user_id
                      FROM routes WHERE user_id IS NOT NULL
                      ORDER BY installation_id, created_at DESC) r ON r.installation_id = s.installation_id
                WHERE s.owner_key LIKE 'i:%'
                ON CONFLICT DO NOTHING;
                DELETE FROM saved_monuments s
                USING (SELECT DISTINCT installation_id FROM routes WHERE user_id IS NOT NULL) r
                WHERE s.installation_id = r.installation_id AND s.owner_key LIKE 'i:%';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "pk_saved_monuments",
                table: "saved_monuments");

            migrationBuilder.DropColumn(
                name: "owner_key",
                table: "saved_monuments");

            migrationBuilder.AddPrimaryKey(
                name: "pk_saved_monuments",
                table: "saved_monuments",
                columns: new[] { "installation_id", "monument_id" });
        }
    }
}
