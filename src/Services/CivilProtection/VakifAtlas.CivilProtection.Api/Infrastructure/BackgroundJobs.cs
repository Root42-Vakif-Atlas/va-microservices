using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using VakifAtlas.CivilProtection.Domain;

namespace VakifAtlas.CivilProtection.Api.Infrastructure;

public sealed class CivilProtectionOptions
{
    public const string Section = "CivilProtection";

    /// <summary>
    /// Yalnızca demo/dev: yeni raporlar 2 dk sonra "inceleniyor", 10 dk sonra "iletildi" olur
    /// (mock davranışı). Gerçek sistemde durum VGM panelinden güncellenir.
    /// </summary>
    public bool DemoProgression { get; set; }
}

/// <summary>Saklama süresi dolan ham konumları siler (KVKK) ve demo ilerlemesini uygular.</summary>
internal sealed class ReportMaintenance(
    IServiceScopeFactory scopes,
    IOptions<CivilProtectionOptions> options,
    TimeProvider clock,
    ILogger<ReportMaintenance> logger) : BackgroundService
{
    private static readonly TimeSpan ReviewDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ForwardDelay = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Report maintenance failed");
            }
        }
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CivilProtectionDbContext>();
        var now = clock.GetUtcNow();
        await db.Locations.Where(l => l.DeleteAfter < now).ExecuteDeleteAsync(cancellationToken);

        if (!options.Value.DemoProgression)
        {
            return;
        }

        var reviewBefore = now - ReviewDelay;
        var candidates = await db.Reports
            .Where(r => (r.Status == ReportStatus.Received || r.Status == ReportStatus.InReview) && r.CreatedAt <= reviewBefore)
            .Take(100).ToListAsync(cancellationToken);
        foreach (var report in candidates.Where(r => !r.HasStaffAction))
        {
            if (report.Status == ReportStatus.Received)
            {
                report.MoveTo(ReportStatus.InReview, null, report.CreatedAt + ReviewDelay);
            }

            if (report.Status == ReportStatus.InReview && report.CreatedAt + ForwardDelay <= now)
            {
                report.MoveTo(ReportStatus.Forwarded, null, report.CreatedAt + ForwardDelay);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
