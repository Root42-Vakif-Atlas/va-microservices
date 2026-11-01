using Microsoft.EntityFrameworkCore;
using VakifAtlas.CivilProtection.Domain;
using VakifAtlas.Persistence;

namespace VakifAtlas.CivilProtection.Api.Infrastructure;

/// <summary>Demo hesabının iki geçmiş raporu (biri sonuçlanmış, VGM notlu) — Flutter mock'uyla aynı.</summary>
internal sealed class DemoReportSeeder(CivilProtectionDbContext db) : IDataSeeder
{
    private const string DemoUser = "usr_demo_001";

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        if (await db.Reports.AnyAsync(r => r.OwnerId == DemoUser, cancellationToken))
        {
            return;
        }

        var first = DamageReport.Submit("rpt_seed_0001", "SK-2026-000101", DemoUser, "kizlaragasi-hani", "vandalism", "medium",
            "Avluya açılan doğu kapısının sağındaki taş duvarda sprey boyayla yazılar var. Yaklaşık iki metrelik bir alanı kaplıyor.",
            [], new LocationCheck(LocationVerification.OnSite, 18), At("2026-09-14T08:20:00Z"));
        first.MoveTo(ReportStatus.InReview, null, At("2026-09-14T09:05:00Z"));
        first.MoveTo(ReportStatus.Forwarded, null, At("2026-09-15T07:40:00Z"));
        first.MoveTo(ReportStatus.Resolved,
            "Duvar yüzeyi uzman ekip tarafından taş dokusuna zarar vermeden temizlendi. Bildiriminiz için teşekkür ederiz.",
            At("2026-09-29T13:10:00Z"));

        var second = DamageReport.Submit("rpt_seed_0002", "SK-2026-000102", DemoUser, "bursa-ulu-camii", "structuralDamage", "high",
            "Kuzey cephedeki pencere kemerinin üstünde son haftalarda belirgin şekilde büyüyen bir çatlak var; altına taş kırıntıları düşmüş.",
            [], LocationCheck.NotProvided, At("2026-10-10T15:45:00Z"));
        second.MoveTo(ReportStatus.InReview, null, At("2026-10-11T08:30:00Z"));

        db.Reports.AddRange(first, second);
        await db.SaveChangesAsync(cancellationToken);

        // Yeni raporların sırası seed'den sonra başlar.
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO reference_counters (year, value) VALUES (2026, 102)
            ON CONFLICT (year) DO UPDATE SET value = GREATEST(reference_counters.value, 102)
            """, cancellationToken);
    }

    private static DateTimeOffset At(string value) => DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
}
