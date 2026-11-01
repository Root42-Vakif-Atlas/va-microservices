namespace VakifAtlas.Gamification.Domain;

/// <summary>Rozet tanımı; <see cref="Kind"/> <c>null</c> → farklı il sayısı. Simge istemcide izinli eşlemedir.</summary>
public sealed record Achievement(string Id, string Icon, ContributionKind? Kind, int Target,
    (string Tr, string En) TitleText, (string Tr, string En) DescriptionText)
{
    public int Progress(IReadOnlyDictionary<ContributionKind, int> counts, int cityCount) =>
        Kind is { } kind ? counts.GetValueOrDefault(kind) : cityCount;

    public string Title(string language) => language == "en" ? TitleText.En : TitleText.Tr;

    public string Description(string language) => language == "en" ? DescriptionText.En : DescriptionText.Tr;
}

public static class Achievements
{
    public static readonly IReadOnlyList<Achievement> All =
    [
        new("first_route", "route", ContributionKind.RouteCreated, 1,
            ("İlk Rota", "First Route"), ("İlk kültür rotanızı oluşturun.", "Create your first cultural route.")),
        new("route_master", "route_master", ContributionKind.RouteCreated, 5,
            ("Rota Ustası", "Route Master"), ("5 kültür rotası oluşturun.", "Create 5 cultural routes.")),
        new("first_memory", "memory", ContributionKind.MemoryShared, 1,
            ("İlk Anı", "First Memory"), ("Anı Köşesi'nde ilk anınızı paylaşın.", "Share your first memory in the Memory Corner.")),
        new("memory_book", "memory_book", ContributionKind.MemoryShared, 10,
            ("Anı Defteri", "Memory Book"), ("10 anı paylaşın.", "Share 10 memories.")),
        new("heritage_watcher", "report", ContributionKind.DamageReported, 1,
            ("Miras Gözcüsü", "Heritage Watcher"),
            ("Bir eserdeki hasarı ilk kez bildirin.", "Report damage to a monument for the first time.")),
        new("volunteer_guardian", "guardian", ContributionKind.DamageReported, 5,
            ("Gönüllü Koruyucu", "Volunteer Guardian"), ("5 hasar bildirimi gönderin.", "Send 5 damage reports.")),
        new("city_explorer", "explorer", null, 3,
            ("Şehir Kâşifi", "City Explorer"),
            ("3 farklı ildeki eserlere katkıda bulunun.", "Contribute to monuments in 3 different provinces.")),
        new("anatolia_traveler", "traveler", null, 7,
            ("Anadolu Gezgini", "Anatolian Traveller"),
            ("7 farklı ildeki eserlere katkıda bulunun.", "Contribute to monuments in 7 different provinces.")),
    ];
}
