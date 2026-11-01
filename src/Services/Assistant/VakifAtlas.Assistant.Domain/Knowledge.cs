namespace VakifAtlas.Assistant.Domain;

public sealed record Text(string Tr, string En)
{
    public string Resolve(string language) => language == "en" ? En : Tr;
}

/// <summary>
/// Rehberin bilgi tabanındaki eser kaydı (katalogdan <c>MonumentUpserted</c> olaylarıyla
/// eşitlenir). Yanıt yalnızca bu alanlardan kurulur; kayıt dışı bilgi üretilmez.
/// </summary>
public sealed record GuideMonument(
    string Id,
    Text Name,
    string Category,
    string Period,
    string City,
    string District,
    string Address,
    Text ShortDescription,
    Text History,
    Text ConstructionDate,
    Text Founder,
    Text? Architect,
    bool IsUnescoWorldHeritage);

public enum GuideDestination
{
    Map,
    RoutePlanner,
    RouteBuilder,
    CivilProtection,
    Settings,
    Accessibility,
}

/// <summary>Tipli, izinli eylem (OWASP LLM01/LLM02): sunucu serbest URL döndürmez.</summary>
public abstract record GuideAction(string Type);

public sealed record MonumentAction(string Type, string MonumentId, string MonumentName) : GuideAction(Type);

public sealed record ReportDamageAction() : GuideAction("reportDamage");

public sealed record SearchAction(IReadOnlyList<string> Categories, IReadOnlyList<string> Periods, IReadOnlyList<string> Cities,
    int Total) : GuideAction("searchMonuments");

public sealed record SectionAction(string Destination) : GuideAction("openSection");

public sealed record GuideAnswer(
    string Text,
    IReadOnlyList<GuideMonument> Sources,
    IReadOnlyList<GuideAction> Actions,
    IReadOnlyList<string> Suggestions,
    string? Notice = null,
    string? FocusMonumentId = null);

/// <summary>Soru kuralları (istemcideki <c>GuideRules</c>): 1–500 karakter.</summary>
public static class GuideRules
{
    public const int MaxQuestionLength = 500;
    public const int MaxQuestionsPerMinute = 20;
    public const int MaxActions = 4;

    public static readonly TimeSpan ConversationTtl = TimeSpan.FromMinutes(30);

    public static bool IsValidQuestion(string? message) =>
        message is not null && message.Trim().Length > 0 && message.Length <= MaxQuestionLength;
}
