using Microsoft.EntityFrameworkCore;
using VakifAtlas.CatalogClient;
using VakifAtlas.Contracts;
using VakifAtlas.Messaging;
using VakifAtlas.Routes.Api.Infrastructure;
using VakifAtlas.Routes.Domain;
using VakifAtlas.ServiceDefaults.Http;
using VakifAtlas.ServiceDefaults.Redis;
using VakifAtlas.SharedKernel;

namespace VakifAtlas.Routes.Api.Features;

public sealed record BreakRequest(string? Kind, int? Minutes);

public sealed record StopRequest(string? MonumentId, int? VisitMinutes, List<BreakRequest>? Breaks);

public sealed record RoutePlanRequest(string? Name, string? TravelMode, List<StopRequest>? Stops)
{
    public RoutePlan? ToPlan()
    {
        if (Name is null || Stops is null || Stops.Any(s => s.MonumentId is null || s.VisitMinutes is null ||
                (s.Breaks ?? []).Any(b => b.Kind is null || b.Minutes is null)))
        {
            return null;
        }

        return new RoutePlan(Name, TravelMode ?? TravelModes.Driving, Stops.Select(s => new RouteStopPlan(
            s.MonumentId!, s.VisitMinutes!.Value, (s.Breaks ?? []).Select(b => new RouteBreak(b.Kind!, b.Minutes!.Value)).ToArray())).ToArray());
    }
}

public sealed record StopLocation(double Latitude, double Longitude);

public sealed record StopResponse(string MonumentId, string Name, string City, string District, string Category,
    StopLocation Location, int VisitMinutes, IReadOnlyList<RouteBreak> Breaks);

public sealed record RouteResponse(string Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string TravelMode,
    IReadOnlyList<StopResponse> Stops, IReadOnlyList<RouteLeg> Legs, RouteSummary Summary);

public sealed record RouteOverview(string Id, string Name, DateTimeOffset CreatedAt, int StopCount, IReadOnlyList<string> Cities,
    RouteSummary Summary);

public sealed record RouteList(IReadOnlyList<RouteOverview> Items);

/// <summary><c>routes/preview</c> ve <c>routes/matrix</c> gövdesi: durak eserleri, sırasıyla.</summary>
public sealed record StopSequenceRequest(List<string>? MonumentIds);

/// <summary>Kaydetmeden karayolu bacakları (oluşturucunun ön hesabı).</summary>
public sealed record RoutePreviewResponse(IReadOnlyList<RouteLeg> Legs, int DistanceMeters, int TravelSeconds);

/// <summary>
/// Durak çiftleri arası karayolu süresi ve mesafesi: <c>[i][j]</c> = <c>i</c> → <c>j</c> (oluşturucunun "Sırayı
/// optimize et"i). Yol bulunmayan çift kuş uçuşu tahminiyle doldurulur.
/// </summary>
public sealed record TravelMatrixResponse(int[][] DurationSeconds, int[][] DistanceMeters);

/// <summary>
/// <c>routes</c> (api-contracts.md → Rotalar): <c>X-Installation-Id</c> zorunlu, Bearer isteğe bağlı
/// (<see cref="OwnerScope"/>). Oturum açıksa rotalar hesaba aittir (yeniden kurulumda ve diğer
/// cihazlarda görünür; kurulumun misafir rotaları hesaba taşınır); misafirde yalnızca kurulumun sahipsiz
/// rotaları. Kapsam dışı rota 404 (varlık sızdırılmaz).
/// </summary>
public static class RouteEndpoints
{
    public const string InstallationHeader = OwnerScope.InstallationHeader;
    private const int CreatesPerHour = 30;

    /// <summary>Liste sayfalı değildir; en yeni bu kadar rota döner (sınırsız yanıt yok).</summary>
    public const int MaxListed = 200;

    /// <summary>Oluşturucu her durak değişiminde (seyreltilmiş) ön hesap ister.</summary>
    private const int PreviewsPerHour = 600;
    private const int MatricesPerHour = 120;

    public static RouteGroupBuilder MapRouteEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet(string.Empty, List);
        group.MapPost(string.Empty, Create);
        group.MapPost("preview", Preview);
        group.MapPost("matrix", Matrix);
        group.MapGet("{id}", Detail);
        group.MapPut("{id}", Update);
        group.MapDelete("{id}", Delete);
        return group.AllowAnonymous();
    }

    private static async Task<IResult> List(HttpContext context, RoutesDbContext db, CancellationToken cancellationToken)
    {
        var resolved = await OwnerScope.ResolveAsync(context, db, cancellationToken);
        if (!resolved.IsSuccess)
        {
            return resolved.Error.ToHttpResult();
        }

        var scope = resolved.Value;
        var routes = await scope.Routes(db.Routes.AsNoTracking())
            .OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id)
            .Take(MaxListed)
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(new RouteList(routes.Select(r =>
            new RouteOverview(r.Id, r.Name, r.CreatedAt, r.Stops.Count, r.Cities, r.Summary)).ToArray()));
    }

    private static async Task<IResult> Create(RoutePlanRequest body, HttpContext context, RoutesDbContext db, ICatalogClient catalog,
        LegCalculator legs, IEventPublisher events, IUsageLimiter limiter, TimeProvider clock, CancellationToken cancellationToken)
    {
        var resolved = await OwnerScope.ResolveAsync(context, db, cancellationToken);
        if (!resolved.IsSuccess)
        {
            return resolved.Error.ToHttpResult();
        }

        var scope = resolved.Value;
        var language = RequestLanguage.Resolve(context.Request);
        var plan = await ResolveAsync(body, catalog, language, cancellationToken);
        if (!plan.IsSuccess)
        {
            return plan.Error.ToHttpResult();
        }

        // Kurulum kimliği istemcide üretildiğinden değiştirilebilir: oturum açıksa hesap, ayrıca
        // istemci adresi başına daha geniş bir sınır uygulanır.
        var owner = scope.UserId is { } userId ? $"u:{userId}" : $"i:{scope.Installation:N}";
        var quota = await limiter.TryConsumeAsync($"route-create:{owner}", CreatesPerHour, TimeSpan.FromHours(1), cancellationToken);
        if (quota.Allowed && context.Connection.RemoteIpAddress is { } address)
        {
            quota = await limiter.TryConsumeAsync($"route-create:ip:{address}", CreatesPerHour * 5, TimeSpan.FromHours(1),
                cancellationToken);
        }

        if (!quota.Allowed)
        {
            return ApiError.TooManyRequests(quota.RetryAfterSeconds).ToHttpResult();
        }

        var (routePlan, monuments) = plan.Value;
        var now = clock.GetUtcNow();
        var route = new TravelRoute
        {
            Id = Ids.New("rt"),
            InstallationId = scope.Installation,
            UserId = scope.UserId,
            Name = routePlan.Name,
            CreatedAt = now,
        };
        route.Apply(routePlan, await legs.CalculateAsync(monuments, cancellationToken), legs.Engine, monuments.Select(m => m.City).ToArray(),
            now);
        db.Routes.Add(route);
        if (route.UserId is not null)
        {
            events.Publish(new RouteCreated(Guid.NewGuid(), now, route.UserId, route.Id,
                monuments.Select(m => new ContributionPlace(m.Id, m.City)).ToArray()));
        }

        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Created($"routes/{route.Id}", ToResponse(route, monuments));
    }

    /// <summary>
    /// Kaydetmeden karayolu bacakları; kayıtla aynı hesap ve önbellek (sonraki kayıt anında hesaplanır). Sahiplik
    /// yoktur, kurulum kimliği gerekmez; istemci adresi başına sınırlıdır.
    /// </summary>
    private static async Task<IResult> Preview(StopSequenceRequest body, HttpContext context, ICatalogClient catalog,
        LegCalculator legs, IUsageLimiter limiter, CancellationToken cancellationToken)
    {
        if (await ConsumeAsync(context, limiter, "route-preview", PreviewsPerHour, cancellationToken) is { } limited)
        {
            return limited;
        }

        var stops = await ResolveStopsAsync(body, catalog, RequestLanguage.Resolve(context.Request), cancellationToken);
        if (!stops.IsSuccess)
        {
            return stops.Error.ToHttpResult();
        }

        var calculated = await legs.CalculateAsync(stops.Value, cancellationToken);
        return TypedResults.Ok(new RoutePreviewResponse(calculated, calculated.Sum(l => l.DistanceMeters),
            calculated.Sum(l => l.DurationSeconds)));
    }

    private static async Task<IResult> Matrix(StopSequenceRequest body, HttpContext context, ICatalogClient catalog,
        IRoutingEngine engine, IUsageLimiter limiter, CancellationToken cancellationToken)
    {
        if (await ConsumeAsync(context, limiter, "route-matrix", MatricesPerHour, cancellationToken) is { } limited)
        {
            return limited;
        }

        var resolved = await ResolveStopsAsync(body, catalog, RequestLanguage.Resolve(context.Request), cancellationToken);
        if (!resolved.IsSuccess)
        {
            return resolved.Error.ToHttpResult();
        }

        var stops = resolved.Value;
        var matrix = await engine.MatrixAsync(stops, cancellationToken);
        var durations = new int[stops.Count][];
        var distances = new int[stops.Count][];
        for (var i = 0; i < stops.Count; i++)
        {
            (durations[i], distances[i]) = (new int[stops.Count], new int[stops.Count]);
            for (var j = 0; j < stops.Count; j++)
            {
                var estimate = i == j || (matrix.DurationSeconds[i][j] is not null && matrix.DistanceMeters[i][j] is not null)
                    ? null
                    : RouteEstimate.Straight(stops[i], stops[j]);
                durations[i][j] = matrix.DurationSeconds[i][j] ?? estimate?.DurationSeconds ?? 0;
                distances[i][j] = matrix.DistanceMeters[i][j] ?? estimate?.DistanceMeters ?? 0;
            }
        }

        return TypedResults.Ok(new TravelMatrixResponse(durations, distances));
    }

    private static async Task<IResult?> ConsumeAsync(HttpContext context, IUsageLimiter limiter, string name, int perHour,
        CancellationToken cancellationToken)
    {
        if (context.Connection.RemoteIpAddress is not { } address)
        {
            return null;
        }

        var quota = await limiter.TryConsumeAsync($"{name}:ip:{address}", perHour, TimeSpan.FromHours(1), cancellationToken);
        return quota.Allowed ? null : ApiError.TooManyRequests(quota.RetryAfterSeconds).ToHttpResult();
    }

    /// <summary>Durak dizisi kuralları (2–10 tekil eser), katalogda bulunma ve konum (rota kaydıyla aynı hatalar).</summary>
    private static async Task<Result<IReadOnlyList<CatalogMonument>>> ResolveStopsAsync(StopSequenceRequest body,
        ICatalogClient catalog, string language, CancellationToken cancellationToken)
    {
        if (body.MonumentIds is not { } ids || !RoutePlanRules.IsValidStopSequence(ids))
        {
            return ApiError.BadRequest(ErrorCodes.InvalidRoute);
        }

        var found = await catalog.GetAsync(ids, language, cancellationToken);
        if (ids.Any(id => !found.ContainsKey(id)))
        {
            return ApiError.BadRequest(ErrorCodes.InvalidRoute);
        }

        if (ids.Any(id => found[id].Location is null))
        {
            return ApiError.BadRequest(ErrorCodes.MonumentWithoutLocation);
        }

        return ids.Select(id => found[id]).ToArray();
    }

    private static async Task<IResult> Detail(string id, HttpContext context, RoutesDbContext db, ICatalogClient catalog,
        LegCalculator legs, ILogger<LegCalculator> logger, CancellationToken cancellationToken)
    {
        var resolved = await OwnerScope.ResolveAsync(context, db, cancellationToken);
        if (!resolved.IsSuccess)
        {
            return resolved.Error.ToHttpResult();
        }

        var request = context.Request;
        var route = await resolved.Value.Routes(db.Routes).SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (route is null)
        {
            return ApiError.NotFound().ToHttpResult();
        }

        var found = await catalog.GetAsync(route.Stops.Select(s => s.MonumentId), RequestLanguage.Resolve(request), cancellationToken);
        var monuments = route.Stops.Select(s => found.GetValueOrDefault(s.MonumentId)).ToArray();
        await RefreshLegsAsync(route, monuments, db, legs, logger, cancellationToken);
        return TypedResults.Ok(ToResponse(route, monuments));
    }

    /// <summary>
    /// Bacaklar başka motorla (eski düz çizgi tahmini, önceki OSRM verisi) veya eserin eski konumuyla
    /// hesaplandıysa yeniden hesaplanır. Yönlendirme motoruna ulaşılamazsa kayıtlı bacaklar döner.
    /// </summary>
    private static async Task RefreshLegsAsync(TravelRoute route, IReadOnlyList<CatalogMonument?> monuments, RoutesDbContext db,
        LegCalculator legs, ILogger logger, CancellationToken cancellationToken)
    {
        if (monuments.Any(m => m?.Point is null))
        {
            return;
        }

        var stops = monuments.Select(m => m!).ToArray();
        if (route.LegsAreCurrent(stops.Select(m => m.Point!.Value).ToArray(), legs.Engine))
        {
            return;
        }

        try
        {
            route.RefreshLegs(await legs.CalculateAsync(stops, cancellationToken), legs.Engine);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Route legs could not be refreshed; returning stored legs");
        }
    }

    private static async Task<IResult> Update(string id, RoutePlanRequest body, HttpContext context, RoutesDbContext db,
        ICatalogClient catalog, LegCalculator legs, TimeProvider clock, CancellationToken cancellationToken)
    {
        var resolved = await OwnerScope.ResolveAsync(context, db, cancellationToken);
        if (!resolved.IsSuccess)
        {
            return resolved.Error.ToHttpResult();
        }

        var request = context.Request;
        var route = await resolved.Value.Routes(db.Routes).SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (route is null)
        {
            return ApiError.NotFound().ToHttpResult();
        }

        var plan = await ResolveAsync(body, catalog, RequestLanguage.Resolve(request), cancellationToken);
        if (!plan.IsSuccess)
        {
            return plan.Error.ToHttpResult();
        }

        var (routePlan, monuments) = plan.Value;
        route.Apply(routePlan, await legs.CalculateAsync(monuments, cancellationToken), legs.Engine,
            monuments.Select(m => m.City).ToArray(), clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(ToResponse(route, monuments));
    }

    private static async Task<IResult> Delete(string id, HttpContext context, RoutesDbContext db, CancellationToken cancellationToken)
    {
        var resolved = await OwnerScope.ResolveAsync(context, db, cancellationToken);
        if (!resolved.IsSuccess)
        {
            return resolved.Error.ToHttpResult();
        }

        var deleted = await resolved.Value.Routes(db.Routes).Where(r => r.Id == id).ExecuteDeleteAsync(cancellationToken);
        return deleted == 0 ? ApiError.NotFound().ToHttpResult() : TypedResults.NoContent();
    }

    /// <summary>
    /// Kuralları uygular ve tüm durakların katalogda bulunduğunu doğrular (aksi <c>invalid_route</c>);
    /// konumu girilmemiş eser durak olamaz (<c>monument_without_location</c>).
    /// </summary>
    private static async Task<Result<(RoutePlan Plan, IReadOnlyList<CatalogMonument> Monuments)>> ResolveAsync(
        RoutePlanRequest body, ICatalogClient catalog, string language, CancellationToken cancellationToken)
    {
        var plan = body.ToPlan();
        if (plan is null || RoutePlanRules.Validate(plan) is not null)
        {
            return ApiError.BadRequest(ErrorCodes.InvalidRoute);
        }

        var found = await catalog.GetAsync(plan.Stops.Select(s => s.MonumentId), language, cancellationToken);
        if (plan.Stops.Any(s => !found.ContainsKey(s.MonumentId)))
        {
            return ApiError.BadRequest(ErrorCodes.InvalidRoute);
        }

        if (plan.Stops.Any(s => found[s.MonumentId].Location is null))
        {
            return ApiError.BadRequest(ErrorCodes.MonumentWithoutLocation);
        }

        return (plan, plan.Stops.Select(s => found[s.MonumentId]).ToArray());
    }

    private static RouteResponse ToResponse(TravelRoute route, IReadOnlyList<CatalogMonument?> monuments) => new(
        route.Id, route.Name, route.CreatedAt, route.UpdatedAt, route.TravelMode,
        route.Stops.Select((stop, index) =>
        {
            var monument = monuments[index];
            return new StopResponse(stop.MonumentId, monument?.Name ?? stop.MonumentId, monument?.City ?? route.Cities[index],
                monument?.District ?? string.Empty, monument?.Category ?? "other",
                new StopLocation(monument?.Location?.Latitude ?? 0, monument?.Location?.Longitude ?? 0), stop.VisitMinutes, stop.Breaks);
        }).ToArray(),
        route.Legs, route.Summary);
}
