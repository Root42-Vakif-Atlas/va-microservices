namespace VakifAtlas.Routes.Domain;

public enum RoutePlanError
{
    EmptyName,
    NameTooLong,
    UnsupportedTravelMode,
    TooFewStops,
    TooManyStops,
    DuplicateStop,
    VisitOutOfRange,
    TooManyBreaks,
    BreakOutOfRange,
    UnknownBreakKind,
}

/// <summary>
/// Rota kuralları (istemcideki <c>RoutePlanRules</c> ile aynı; sunucu yetkilidir): 2–10 durak,
/// tekrar yok, ziyaret 15–480 dk, durak başına ≤ 3 mola (15–720 dk), süreler 15 dk adımlı.
/// </summary>
public static class RoutePlanRules
{
    public const int MinStops = 2;
    public const int MaxStops = 10;
    public const int MaxNameLength = 60;
    public const int MaxBreaksPerStop = 3;
    public const int Step = 15;
    public const int MinVisit = 15;
    public const int MaxVisit = 480;
    public const int MinBreak = 15;
    public const int MaxBreak = 720;

    public static RoutePlanError? Validate(RoutePlan plan)
    {
        var name = plan.Name.Trim();
        if (name.Length == 0)
        {
            return RoutePlanError.EmptyName;
        }

        if (name.Length > MaxNameLength)
        {
            return RoutePlanError.NameTooLong;
        }

        if (plan.TravelMode != TravelModes.Driving)
        {
            return RoutePlanError.UnsupportedTravelMode;
        }

        if (plan.Stops.Count < MinStops)
        {
            return RoutePlanError.TooFewStops;
        }

        if (plan.Stops.Count > MaxStops)
        {
            return RoutePlanError.TooManyStops;
        }

        if (plan.Stops.Select(s => s.MonumentId).Distinct(StringComparer.Ordinal).Count() != plan.Stops.Count)
        {
            return RoutePlanError.DuplicateStop;
        }

        foreach (var stop in plan.Stops)
        {
            if (!InSteps(stop.VisitMinutes, MinVisit, MaxVisit))
            {
                return RoutePlanError.VisitOutOfRange;
            }

            if (stop.Breaks.Count > MaxBreaksPerStop)
            {
                return RoutePlanError.TooManyBreaks;
            }

            foreach (var pause in stop.Breaks)
            {
                if (!BreakKinds.All.Contains(pause.Kind))
                {
                    return RoutePlanError.UnknownBreakKind;
                }

                if (!InSteps(pause.Minutes, MinBreak, MaxBreak))
                {
                    return RoutePlanError.BreakOutOfRange;
                }
            }
        }

        return null;
    }

    private static bool InSteps(int minutes, int min, int max) => minutes >= min && minutes <= max && minutes % Step == 0;
}
