using VakifAtlas.SharedKernel;

namespace VakifAtlas.Gamification.Domain;

public enum ContributionKind
{
    RouteCreated,
    MemoryShared,
    DamageReported,
}

/// <summary>Bir katkı olayı (rota/anı/rapor servislerinin olaylarından).</summary>
public sealed class Contribution
{
    /// <summary>Kaynak olayın kimliği: aynı olay iki kez sayılmaz.</summary>
    public Guid Id { get; init; }

    public required string UserId { get; init; }

    public ContributionKind Kind { get; init; }

    public required string ReferenceId { get; init; }

    public List<string> Cities { get; init; } = [];

    public DateTimeOffset OccurredAt { get; init; }
}

/// <summary>Kullanıcıya kutlanmış (görüldü) kazanım: rozet kimliği veya <c>level:N</c>.</summary>
public sealed class Acknowledgement
{
    public required string UserId { get; init; }

    public required string ItemId { get; init; }
}

public sealed record AchievementProgress(string Id, string Title, string Description, string Icon, int Progress, int Target,
    DateTimeOffset? UnlockedAt);

public sealed record GamificationProfile(int Level, string LevelTitle, int Xp, int LevelStartXp, int? NextLevelXp,
    IReadOnlyList<AchievementProgress> Achievements, IReadOnlyList<string> Unseen);

/// <summary>
/// Sunucu yetkili XP/seviye/rozet hesabı (ADR 41) — mock'taki kuralların birebir karşılığı.
/// XP: rota 50, anı 30, rapor 40 (türe göre Türkiye günü başına 3/5/3 sınır), bir ildeki
/// esere ilk katkı +20. Sınırı aşan katkı rozet ilerlemesine sayılır, XP vermez; silme XP'yi geri almaz.
/// </summary>
public static class ProgressCalculator
{
    public const int NewCityXp = 20;

    public static readonly IReadOnlyList<int> LevelThresholds = [0, 100, 250, 500, 900, 1400, 2000];

    private static readonly Dictionary<ContributionKind, (int Xp, int DailyCap)> Rules = new()
    {
        [ContributionKind.RouteCreated] = (50, 3),
        [ContributionKind.MemoryShared] = (30, 5),
        [ContributionKind.DamageReported] = (40, 3),
    };

    private static readonly Dictionary<string, string[]> LevelTitles = new(StringComparer.Ordinal)
    {
        ["tr"] = ["Meraklı", "Gezgin", "Kâşif", "Miras Dostu", "Miras Bekçisi", "Vakıf Elçisi", "Atlas Ustası"],
        ["en"] = ["Curious", "Wanderer", "Explorer", "Heritage Friend", "Heritage Keeper", "Foundation Ambassador", "Atlas Master"],
    };

    public static GamificationProfile Compute(IEnumerable<Contribution> contributions, IReadOnlySet<string> seen, string language)
    {
        var xp = 0;
        var perDay = new Dictionary<(ContributionKind, DateOnly), int>();
        var cities = new HashSet<string>(StringComparer.Ordinal);
        var counts = new Dictionary<ContributionKind, int>();
        var unlocked = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);

        foreach (var contribution in contributions.OrderBy(c => c.OccurredAt).ThenBy(c => c.Id))
        {
            var rule = Rules[contribution.Kind];
            var key = (contribution.Kind, TurkeyTime.DateOf(contribution.OccurredAt));
            var today = perDay[key] = perDay.GetValueOrDefault(key) + 1;
            if (today <= rule.DailyCap)
            {
                xp += rule.Xp;
            }

            foreach (var city in contribution.Cities)
            {
                if (cities.Add(city))
                {
                    xp += NewCityXp;
                }
            }

            counts[contribution.Kind] = counts.GetValueOrDefault(contribution.Kind) + 1;
            foreach (var achievement in Achievements.All)
            {
                if (achievement.Progress(counts, cities.Count) >= achievement.Target)
                {
                    unlocked.TryAdd(achievement.Id, contribution.OccurredAt);
                }
            }
        }

        var level = LevelThresholds.Count(start => xp >= start);
        var titles = LevelTitles[language == "en" ? "en" : "tr"];
        var unseen = Achievements.All.Where(a => unlocked.ContainsKey(a.Id) && !seen.Contains(a.Id)).Select(a => a.Id)
            .Concat(Enumerable.Range(2, Math.Max(0, level - 1)).Select(l => $"level:{l}").Where(id => !seen.Contains(id)))
            .ToArray();

        return new GamificationProfile(
            level, titles[level - 1], xp, LevelThresholds[level - 1], level < LevelThresholds.Count ? LevelThresholds[level] : null,
            Achievements.All.Select(a => new AchievementProgress(a.Id, a.Title(language), a.Description(language), a.Icon,
                Math.Clamp(a.Progress(counts, cities.Count), 0, a.Target), a.Target, unlocked.TryGetValue(a.Id, out var at) ? at : null))
                .ToArray(),
            unseen);
    }
}
