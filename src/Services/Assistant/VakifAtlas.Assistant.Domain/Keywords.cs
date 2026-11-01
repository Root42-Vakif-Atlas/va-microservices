using System.Text.RegularExpressions;
using VakifAtlas.SharedKernel;

namespace VakifAtlas.Assistant.Domain;

/// <summary>Katlanmış, sözcüklere ayrılmış soru.</summary>
internal sealed partial class GuideQuery
{
    private readonly string _padded;

    public GuideQuery(string question)
    {
        Tokens = Tokenize(question);
        _padded = $" {string.Join(' ', Tokens)} ";
    }

    public IReadOnlyList<string> Tokens { get; }

    /// <summary>
    /// Sözcük <paramref name="term"/> ile aynı, çekimli hali ("süleymaniye'yi") veya yarım yazılmış hali
    /// ("süleymani"; en az 5 harf ve sözcüğün dörtte üçü — "Sultan" "Sultanhanı" ile eşleşmez).
    /// </summary>
    public bool MatchesTerm(string term) => Tokens.Any(token =>
        token == term ||
        (term.Length >= 4 && token.StartsWith(term, StringComparison.Ordinal)) ||
        (token.Length >= 5 && token.Length * 4 >= term.Length * 3 && term.StartsWith(token, StringComparison.Ordinal)));

    public bool HasPhrase(string phrase) => _padded.Contains($" {phrase}", StringComparison.Ordinal);

    public static IReadOnlyList<string> Tokenize(string text) =>
        NonAlphanumeric().Replace(TurkishText.Fold(text), " ").Split(' ', StringSplitOptions.RemoveEmptyEntries);

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonAlphanumeric();
}

/// <summary>Niyet sözlüğü: tam sözcükler, sözcük başları (çekim ekleri) ve ifadeler.</summary>
internal sealed class Keywords(string[]? words = null, string[]? prefixes = null, string[]? phrases = null)
{
    public IReadOnlySet<string> Words { get; } = new HashSet<string>(words ?? [], StringComparer.Ordinal);

    public IReadOnlyList<string> Prefixes { get; } = prefixes ?? [];

    public IReadOnlyList<string> Phrases { get; } = phrases ?? [];

    public static Keywords Union(params Keywords[] parts) => new(
        parts.SelectMany(p => p.Words).ToArray(),
        parts.SelectMany(p => p.Prefixes).ToArray(),
        parts.SelectMany(p => p.Phrases).ToArray());

    public bool Matches(GuideQuery query) =>
        query.Tokens.Any(token => Words.Contains(token) || Prefixes.Any(p => token.StartsWith(p, StringComparison.Ordinal))) ||
        Phrases.Any(query.HasPhrase);
}

/// <summary>Niyet sözlükleri (katlanmış: ç→c, ğ→g, ı→i, ö→o, ş→s, ü→u).</summary>
internal static class Intents
{
    public static readonly Keywords Emergency = new(
        ["acil", "fire", "112", "alev"],
        ["yangin", "yaniyor", "yandi", "yanmis", "cokuyor", "coktu", "cokecek", "yikiliyor", "yikildi", "deprem", "emergenc",
            "collaps", "earthquake", "burning"]);

    public static readonly Keywords Damage = new(
        ["bildir", "report", "ihbar", "kirik", "broken"],
        ["hasar", "catlak", "catlam", "yikik", "vandal", "tahrip", "sikayet", "bildir", "damage", "crack", "graffit", "stolen", "calin"],
        ["sivil koruma", "civil protection"]);

    public static readonly Keywords TextSize = new(
        ["font"],
        ["buyut", "kucult", "enlarge"],
        ["yazi boyut", "metin boyut", "yazilari buyut", "yazi buyut", "text size", "font size", "larger text", "text larger"]);

    public static readonly Keywords Theme = new(["dark", "koyu", "karanlik"], ["tema", "theme"]);

    public static readonly Keywords Contrast = new(prefixes: ["kontrast", "contrast"]);

    public static readonly Keywords ColorVision = new(
        prefixes: ["daltoni", "colorblind", "colourblind", "protanop", "deuteranop"],
        phrases: ["renk kor", "renk gorme", "color blind", "colour blind", "color vision", "colour vision"]);

    public static readonly Keywords Motion = new(
        prefixes: ["titresim", "vibrat", "animasyon", "animation"],
        phrases: ["hareketi azalt", "reduce motion"]);

    public static readonly Keywords Language = new(["dil", "dili", "language", "ingilizce", "english", "turkish"]);

    /// <summary>Erişilebilirlik sayfasının konuları (metin boyutu, kontrast, renk görme, hareket, titreşim).</summary>
    public static readonly Keywords Accessibility = Keywords.Union(
        new Keywords(prefixes: ["erisilebil", "accessib"]), TextSize, Contrast, ColorVision, Motion);

    public static readonly Keywords Settings = Keywords.Union(
        new Keywords(prefixes: ["ayar", "setting"]), Accessibility, Theme, Language);

    public static readonly Keywords Memories = new(
        ["ani", "anilar", "anilari", "anisi", "aniyi", "anim", "anilarim", "anilarini", "aninizi", "memory", "memories"],
        ["hatira", "memor"]);

    public static readonly Keywords Route = new(
        ["plan", "tur", "tour", "trip", "gezmek", "gezi"],
        ["rota", "guzergah", "itinerar", "route", "planla", "optimiz", "optimis"]);

    public static readonly Keywords Optimize = new(prefixes: ["optimiz", "optimis", "sirala"], phrases: ["en kisa", "shortest"]);

    public static readonly Keywords Others = new(["diger", "baska", "other", "another", "more"]);

    public static readonly Keywords Listing = new(
        ["list", "listele", "hangi", "neler", "nereler", "kac", "tumu", "hepsi", "all", "sites", "places"],
        ["eser", "yapilar", "monument", "gezilecek", "gorulecek"],
        ["how many"]);

    public static readonly Keywords Map = new(
        ["near", "around", "map", "konum", "location", "nearby"],
        ["yakin", "cevrem", "etrafim", "harita", "konumum"]);

    public static readonly Keywords Help = new(
        ["yardim", "help", "kimsin"],
        phrases: ["ne yapabilir", "neler yapabilir", "what can you", "who are you"]);

    public static readonly Keywords Thanks = new(["thanks", "thx", "eyv", "sagol", "sagolun"], ["tesekkur", "thank"]);

    public static readonly Keywords Greeting = new(
        ["merhaba", "merhabalar", "selam", "selamlar", "hello", "hi", "hey", "gunaydin", "mrb", "slm"],
        phrases: ["iyi gunler", "iyi aksamlar", "good morning"]);

    /// <summary>Önceki esere gönderme ("bu eserde", "burası").</summary>
    public static readonly Keywords Deictic = new(["bu", "bura", "burasi", "burada", "orada", "this", "here", "it"]);

    public static readonly Keywords ArchitectFacet = new(prefixes: ["mimar", "architect"]);

    public static readonly Keywords FounderFacet = new(
        ["kim", "kimin", "who", "bani", "banisi", "kurucu", "kurucusu"], ["yaptir", "founder", "commission"]);

    public static readonly Keywords UnescoFacet = new(prefixes: ["unesco"], phrases: ["dunya miras", "world herit"]);

    public static readonly Keywords HistoryFacet = new(["history", "story", "gecmisi"], ["tarihce", "hikaye", "anlat"]);

    public static readonly Keywords LocationFacet = new(
        ["nerede", "nerde", "where", "adres", "adresi", "address"], ["haritada"], ["nasil gid", "how do i get", "how to get"]);

    public static readonly Keywords PeriodFacet = new(["era"], ["donem", "period"]);

    public static readonly Keywords DateFacet = new(
        ["zaman", "when", "year", "yil", "yili", "date", "tarih", "tarihinde", "century", "yuzyil"],
        ["insa", "yapim", "yapil", "built", "construct"]);

    public static readonly IReadOnlyDictionary<string, Keywords> Categories = new Dictionary<string, Keywords>
    {
        ["mosque"] = new(["mescit"], ["cami", "mosque"]),
        ["complex"] = new(prefixes: ["kulliye", "complex"]),
        ["madrasa"] = new(prefixes: ["medrese", "madrasa", "madrassa"]),
        ["inn"] = new(["han", "hani", "hanlar", "hanlari", "inn", "inns"]),
        ["caravanserai"] = new(prefixes: ["kervansaray", "caravanserai", "caravansar"]),
        ["bath"] = new(prefixes: ["hamam", "bath", "hammam"]),
        ["fountain"] = new(["sebil"], ["cesme", "fountain"]),
        ["tomb"] = new(prefixes: ["turbe", "tomb", "mausole"]),
        ["hospital"] = new(prefixes: ["darussifa", "sifahane", "hospital"]),
        ["imaret"] = new(prefixes: ["imaret", "daruzziyafe", "asevi"]),
    };

    public static readonly IReadOnlyDictionary<string, Keywords> Periods = new Dictionary<string, Keywords>
    {
        ["seljuk"] = new(prefixes: ["selcuk", "seljuk", "seljuq"]),
        ["beylik"] = new(prefixes: ["beylik", "principalit", "mengucek"]),
        ["ottoman"] = new(prefixes: ["osmanli", "ottoman"]),
    };
}

internal enum Facet
{
    Founder,
    Architect,
    Date,
    Location,
    Period,
    Unesco,
    History,
}

internal static class Facets
{
    public static Facet? Of(GuideQuery q) =>
        Intents.ArchitectFacet.Matches(q) ? Facet.Architect
        : Intents.FounderFacet.Matches(q) ? Facet.Founder
        : Intents.UnescoFacet.Matches(q) ? Facet.Unesco
        : Intents.HistoryFacet.Matches(q) ? Facet.History
        : Intents.LocationFacet.Matches(q) ? Facet.Location
        : Intents.PeriodFacet.Matches(q) ? Facet.Period
        : Intents.DateFacet.Matches(q) ? Facet.Date
        : null;
}

/// <summary>Soruda geçen il, yapı türü, dönem ve UNESCO ölçütleri.</summary>
internal sealed record GuideFilter(IReadOnlyList<string> Categories, IReadOnlyList<string> Periods, IReadOnlyList<string> Cities,
    bool Unesco)
{
    public static readonly GuideFilter Empty = new([], [], [], false);

    public bool IsEmpty => Categories.Count == 0 && Periods.Count == 0 && Cities.Count == 0 && !Unesco;

    public static GuideFilter Of(GuideQuery q, IReadOnlyList<GuideMonument> monuments) => new(
        Intents.Categories.Where(c => c.Value.Matches(q)).Select(c => c.Key).ToArray(),
        Intents.Periods.Where(p => p.Value.Matches(q)).Select(p => p.Key).ToArray(),
        monuments.Select(m => m.City).Distinct(StringComparer.Ordinal).Where(city => q.MatchesTerm(TurkishText.Fold(city))).ToArray(),
        Intents.UnescoFacet.Matches(q));

    public IReadOnlyList<GuideMonument> Apply(IEnumerable<GuideMonument> monuments) => monuments
        .Where(m => (Categories.Count == 0 || Categories.Contains(m.Category)) &&
                    (Periods.Count == 0 || Periods.Contains(m.Period)) &&
                    (Cities.Count == 0 || Cities.Contains(m.City)) &&
                    (!Unesco || m.IsUnescoWorldHeritage))
        .OrderBy(m => m.Name.Tr, TurkishText.Comparer)
        .ToArray();

    public string Describe(Copy copy) => string.Join(" · ",
        Cities.Concat(Periods.Select(copy.Period)).Concat(Categories.Select(copy.Category)).Concat(Unesco ? ["UNESCO"] : []));
}

/// <summary>Yanıt metinleri (içerik dilinde).</summary>
internal sealed class Copy(string language)
{
    public string Language { get; } = language == "en" ? "en" : "tr";

    public string T(string tr, string en) => Language == "en" ? en : tr;

    public IReadOnlyList<string> Starters =>
    [
        T("Süleymaniye Camii'ni kim yaptırdı?", "Who built the Süleymaniye Mosque?"),
        T("Konya'daki Selçuklu eserleri", "Seljuk monuments in Konya"),
        T("Rota nasıl planlarım?", "How do I plan a route?"),
        T("Hasar nasıl bildiririm?", "How do I report damage?"),
    ];

    public string Category(string category) => category switch
    {
        "mosque" => T("Cami", "Mosque"),
        "complex" => T("Külliye", "Complex"),
        "madrasa" => T("Medrese", "Madrasa"),
        "inn" => T("Han", "Inn"),
        "caravanserai" => T("Kervansaray", "Caravanserai"),
        "bath" => T("Hamam", "Bath"),
        "fountain" => T("Çeşme", "Fountain"),
        "tomb" => T("Türbe", "Tomb"),
        "hospital" => T("Darüşşifa", "Hospital"),
        "imaret" => T("İmaret", "Public kitchen"),
        _ => T("Diğer", "Other"),
    };

    public string Period(string period) => period switch
    {
        "seljuk" => T("Selçuklu", "Seljuk"),
        "beylik" => T("Beylikler", "Beylik"),
        "ottoman" => T("Osmanlı", "Ottoman"),
        _ => T("Diğer", "Other"),
    };
}
