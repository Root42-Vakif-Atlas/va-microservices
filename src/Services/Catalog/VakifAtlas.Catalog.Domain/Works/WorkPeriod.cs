namespace VakifAtlas.Catalog.Domain.Works;

// historical period; not in the bulk file, set by staff (unknown → other)
public static class WorkPeriod
{
    public const string Other = "other";

    public static readonly IReadOnlyList<string> All = ["seljuk", "beylik", "ottoman", Other];
}
