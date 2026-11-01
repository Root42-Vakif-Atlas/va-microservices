using Microsoft.EntityFrameworkCore;
using VakifAtlas.Routes.Api.Infrastructure;
using VakifAtlas.Routes.Domain;
using VakifAtlas.ServiceDefaults.Auth;
using VakifAtlas.SharedKernel;

namespace VakifAtlas.Routes.Api.Features;

/// <summary>
/// Rotaların ve kaydedilen eserlerin sahibi: oturum açıksa hesap, değilse kurulum (ADR 21/40).
/// <list type="bullet">
/// <item><c>X-Installation-Id</c> (UUID v4) her istekte zorunlu.</item>
/// <item>Bearer gönderilmiş ama geçersizse (süresi dolmuş) <c>401</c>: istemci anahtarı yeniler ve
/// tekrar dener; aksi halde hesabın verisi sessizce misafir görünümüne düşerdi.</item>
/// <item>Oturum açık ilk istekte kurulumun sahipsiz (misafir) kayıtları hesaba taşınır; böylece
/// uygulama silinip kurulsa da (yeni kurulum kimliği) veya başka cihazdan girilse de kaybolmaz.</item>
/// <item>Misafir, kurulumun yalnızca sahipsiz kayıtlarını görür; oturum kapatılınca hesabın
/// kayıtları bu cihazda görünmez (ortak cihaz gizliliği).</item>
/// </list>
/// </summary>
public sealed record OwnerScope(Guid Installation, string? UserId)
{
    public const string InstallationHeader = "X-Installation-Id";

    public string SavedOwnerKey => UserId is null ? GuestKey(Installation) : AccountKey(UserId);

    public static string GuestKey(Guid installation) => $"i:{installation:N}";

    public static string AccountKey(string userId) => $"u:{userId}";

    public IQueryable<TravelRoute> Routes(IQueryable<TravelRoute> routes) => UserId is null
        ? routes.Where(r => r.InstallationId == Installation && r.UserId == null)
        : routes.Where(r => r.UserId == UserId);

    public static async Task<Result<OwnerScope>> ResolveAsync(HttpContext context, RoutesDbContext db,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(context.Request.Headers[InstallationHeader], out var installation) || installation.Version != 4)
        {
            return ApiError.BadRequest(ErrorCodes.InstallationIdRequired);
        }

        var userId = context.OptionalUserId();
        if (userId is null && context.Request.Headers.Authorization.Count > 0)
        {
            return ApiError.Unauthorized();
        }

        var scope = new OwnerScope(installation, userId);
        if (userId is not null)
        {
            await scope.ClaimGuestDataAsync(db, cancellationToken);
        }

        return scope;
    }

    /// <summary>
    /// Kurulumun misafir kayıtlarını hesaba taşır (idempotent). Kaydedilenlerde hesapta zaten olan eser
    /// atlanır (erken kaydetme zamanı korunur); toplam sınırı aşılırsa en yeniler kalır.
    /// </summary>
    private async Task ClaimGuestDataAsync(RoutesDbContext db, CancellationToken cancellationToken)
    {
        await db.Routes.Where(r => r.InstallationId == Installation && r.UserId == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(r => r.UserId, UserId), cancellationToken);

        var guestKey = GuestKey(Installation);
        if (!await db.SavedMonuments.AnyAsync(s => s.OwnerKey == guestKey, cancellationToken))
        {
            return;
        }

        // Aynı hesabın eşzamanlı istekleri birleştirmeyi aynı anda yapıp birbirinin eklediği satırla
        // birincil anahtar çakışmasına (500) düşmesin: hesap başına işlem kilidi, okumalar kilit içinde.
        var accountKey = SavedOwnerKey;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtext({accountKey}))", cancellationToken);
        var guest = await db.SavedMonuments.Where(s => s.OwnerKey == guestKey).ToListAsync(cancellationToken);
        if (guest.Count == 0)
        {
            return;
        }

        var account = await db.SavedMonuments.Where(s => s.OwnerKey == accountKey).ToListAsync(cancellationToken);
        var merged = account.Concat(guest.Select(g => new SavedMonument
        {
            OwnerKey = accountKey,
            InstallationId = g.InstallationId,
            MonumentId = g.MonumentId,
            SavedAt = g.SavedAt,
        }))
            .GroupBy(s => s.MonumentId, StringComparer.Ordinal)
            .Select(group => group.MinBy(s => s.SavedAt)!)
            .OrderByDescending(s => s.SavedAt).ThenBy(s => s.MonumentId, StringComparer.Ordinal)
            .Take(SavedMonumentRules.MaxSaved)
            .ToList();

        db.SavedMonuments.RemoveRange(guest);
        db.SavedMonuments.RemoveRange(account);
        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();
        db.SavedMonuments.AddRange(merged);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
