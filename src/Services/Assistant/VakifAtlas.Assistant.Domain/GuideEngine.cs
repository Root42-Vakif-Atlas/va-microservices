using System.Text.RegularExpressions;
using VakifAtlas.SharedKernel;

namespace VakifAtlas.Assistant.Domain;

/// <summary>
/// Kural tabanlı Dijital Rehber (istemcideki <c>MockGuideEngine</c>'in birebir portu).
/// 1. Soru Türkçe katlanıp sözcüklere ayrılır. 2. Niyet anahtar sözcüklerle bulunur
/// (acil › hasar › ayarlar › anı › rota › eser › listeleme › harita › yardım).
/// 3. Eser, il, tür, dönem ve kişi (bani/mimar) bilgi tabanında aranır; yanıt yalnızca
/// bulunan kayıtların alanlarından kurulur ve kayıtlar kaynak olarak döner. 4. Son
/// konuşulan eser bağlamda tutulur ("Mimarı kim?").
/// </summary>
public sealed partial class GuideEngine
{
    public const int MaxListed = 5;
    public const int MaxSuggestions = 4;

    private static readonly HashSet<string> GenericNameTokens = new(StringComparer.Ordinal)
    {
        "cami", "camii", "mosque", "han", "hani", "inn", "kulliyesi", "complex", "kervansarayi", "caravanserai", "and", "the",
        "great", "grand", "hospital",
    };

    private static readonly HashSet<string> GenericPersonTokens = new(StringComparer.Ordinal)
    {
        "sultan", "sultani", "mimar", "buyuk", "great", "onarimi", "restoration", "olarak", "zaviye", "dervish", "lodge", "the",
        "and", "magnificent", "selcuklu", "seljuk", "osmanli", "ottoman",
    };

    private readonly IReadOnlyList<GuideMonument> _monuments;
    private readonly Dictionary<string, HashSet<string>> _nameTerms = new(StringComparer.Ordinal);

    /// <summary>Adın yapı türü sözcükleri: eşit puanlı adaylarda soruda geçen tür öne çıkar.</summary>
    private readonly Dictionary<string, HashSet<string>> _typeTerms = new(StringComparer.Ordinal);

    /// <summary>Tüm bani/mimar sözcükleri: adı yalnızca kişi sözcükleriyle eşleşen eser aday sayılmaz.</summary>
    private readonly HashSet<string> _personVocabulary = new(GenericPersonTokens, StringComparer.Ordinal);
    private readonly Dictionary<string, List<(Text Person, HashSet<string> Terms)>> _personTerms = new(StringComparer.Ordinal);

    public GuideEngine(IReadOnlyList<GuideMonument> monuments)
    {
        _monuments = monuments;
        var cities = monuments.Select(m => TurkishText.Fold(m.City)).ToHashSet(StringComparer.Ordinal);
        foreach (var monument in monuments)
        {
            _nameTerms[monument.Id] = new[] { monument.Name.Tr, monument.Name.En }
                .SelectMany(GuideQuery.Tokenize)
                .Where(token => token.Length >= 3 && !GenericNameTokens.Contains(token) && !cities.Contains(token))
                .ToHashSet(StringComparer.Ordinal);
            _typeTerms[monument.Id] = new[] { monument.Name.Tr, monument.Name.En }
                .SelectMany(GuideQuery.Tokenize)
                .Where(GenericNameTokens.Contains)
                .ToHashSet(StringComparer.Ordinal);
            _personTerms[monument.Id] = new[] { monument.Founder, monument.Architect }
                .OfType<Text>()
                .Select(person => (person, new[] { person.Tr, person.En }.SelectMany(GuideQuery.Tokenize)
                    .Where(token => token.Length >= 4 && !GenericPersonTokens.Contains(token)).ToHashSet(StringComparer.Ordinal)))
                .ToList();
            _personVocabulary.UnionWith(_personTerms[monument.Id].SelectMany(p => p.Terms));
        }
    }

    public int Count => _monuments.Count;

    public GuideAnswer Answer(string question, string language, string? contextMonumentId)
    {
        var q = new GuideQuery(question);
        var copy = new Copy(language);
        var named = MatchNames(q);
        var context = _monuments.FirstOrDefault(m => m.Id == contextMonumentId);
        var deictic = Intents.Deictic.Matches(q);
        var monument = named.Count == 1 ? named[0] : named.Count == 0 && deictic ? context : null;

        if (Intents.Emergency.Matches(q))
        {
            return EmergencyAnswer(copy, monument);
        }

        if (Intents.Damage.Matches(q))
        {
            return DamageAnswer(copy, monument);
        }

        if (Intents.Settings.Matches(q))
        {
            return SettingsAnswer(copy, q);
        }

        if (Intents.Memories.Matches(q))
        {
            return MemoriesAnswer(copy, monument ?? context);
        }

        var filter = GuideFilter.Of(q, _monuments);
        if (Intents.Route.Matches(q))
        {
            return RouteAnswer(copy, q, monument, filter);
        }

        if (named.Count > 1)
        {
            return AmbiguousAnswer(copy, named);
        }

        var facet = Facets.Of(q);
        if (monument is not null)
        {
            return MonumentAnswer(copy, monument, facet);
        }

        var people = MatchPeople(q, copy.Language);
        if (!filter.IsEmpty || people.Monuments.Count > 0)
        {
            // "İstanbul'daki diğer eserler": konuşulan eser listeden çıkarılır.
            return ListAnswer(copy, filter, people, Intents.Others.Matches(q) ? context?.Id : null);
        }

        if (facet is not null && context is not null)
        {
            return MonumentAnswer(copy, context, facet);
        }

        if (Intents.Map.Matches(q))
        {
            return MapAnswer(copy);
        }

        if (Intents.Listing.Matches(q))
        {
            return OverviewAnswer(copy);
        }

        if (Intents.Help.Matches(q))
        {
            return HelpAnswer(copy);
        }

        if (Intents.Thanks.Matches(q))
        {
            return new GuideAnswer(copy.T("Rica ederim! Başka bir sorunuz olursa buradayım.",
                "You're welcome! I'm here if you have another question."), [], [], copy.Starters);
        }

        if (Intents.Greeting.Matches(q))
        {
            return new GuideAnswer(copy.T(
                "Merhaba! Ben Vakıf Atlas'ın Dijital Rehberiyim. Vakıf eserleri hakkındaki sorularınızı VGM kataloğuna dayanarak yanıtlar, sizi rota planlama, Anı Köşesi, hasar bildirimi ve ayarlar gibi bölümlere yönlendiririm.",
                "Hello! I'm the Vakıf Atlas Digital Guide. I answer your questions about foundation monuments from the VGM catalogue and take you to route planning, the Memory Corner, damage reporting and settings."),
                [], [], copy.Starters);
        }

        return FallbackAnswer(copy);
    }

    // ── Yanıtlar ─────────────────────────────────────────────────────────

    private static GuideAnswer EmergencyAnswer(Copy copy, GuideMonument? monument) => new(
        copy.T(
            "Can güvenliği tehdit ediliyorsa hemen 112 Acil Çağrı Merkezi'ni arayın ve bölgeden uzaklaşın. Güvende olduğunuzda durumu Sivil Koruma'ya bildirerek VGM'nin esere müdahale etmesine yardımcı olabilirsiniz.",
            "If anyone is in danger, call 112 Emergency right away and move away from the area. Once you are safe, you can report the situation to Civil Protection so that VGM can act on the monument."),
        Sources(monument),
        [ReportDamage(copy, monument), Section(GuideDestination.CivilProtection)],
        [],
        "emergency",
        monument?.Id);

    private static GuideAnswer DamageAnswer(Copy copy, GuideMonument? monument)
    {
        var name = monument?.Name.Resolve(copy.Language);
        var text = new List<string>
        {
            copy.T(
                "Bir vakıf eserinde hasar, vandalizm veya tehlike gördüyseniz Sivil Koruma'ya bildirebilirsiniz. Bildirim altı adımdır: eser, hasar türü, aciliyet, açıklama ve fotoğraf, isteğe bağlı konum ve gözden geçirme. Giriş yapmanız gerekir; size bir kayıt numarası verilir ve süreci \"Bildirimlerim\"den izleyebilirsiniz.",
                "If you see damage, vandalism or a hazard at a foundation monument, report it to Civil Protection. The report has six steps: monument, damage type, urgency, description and photos, optional location and review. You need to sign in; you get a reference number and can follow progress under \"My reports\"."),
        };
        if (name is not null)
        {
            text.Add(copy.T($"Form, {name} seçili olarak açılır.", $"The form opens with {name} selected."));
        }

        return new GuideAnswer(string.Join("\n\n", text), Sources(monument),
            [ReportDamage(copy, monument), Section(GuideDestination.CivilProtection)],
            [copy.T("Acil durumda ne yapmalıyım?", "What should I do in an emergency?")], null, monument?.Id);
    }

    private static GuideAnswer SettingsAnswer(Copy copy, GuideQuery q)
    {
        var text = new List<string>();
        if (Intents.TextSize.Matches(q))
        {
            text.Add(copy.T("Metin boyutunu Erişilebilirlik sayfasından %200'e kadar büyütebilirsiniz; ekranlar büyük metinde de taşmadan okunur.",
                "You can enlarge text up to 200% on the Accessibility page; screens stay readable at large sizes."));
        }

        if (Intents.Theme.Matches(q))
        {
            text.Add(copy.T("Açık, koyu veya cihazınızı izleyen temayı Ayarlar'dan seçebilirsiniz.",
                "You can choose a light, dark or device-matching theme in Settings."));
        }

        if (Intents.Contrast.Matches(q))
        {
            text.Add(copy.T("Yüksek kontrast metin ve kenarlıkları en az 7:1 kontrastla gösterir; Erişilebilirlik sayfasından açabilirsiniz.",
                "High contrast shows text and borders with at least 7:1 contrast; turn it on from the Accessibility page."));
        }

        if (Intents.ColorVision.Matches(q))
        {
            text.Add(copy.T("Renk görme desteği protanopi, döteranopi ve tritanopi için renkleri ayırt edilebilir hale getirir; haritaya da uygulanır.",
                "Colour vision support makes colours distinguishable for protanopia, deuteranopia and tritanopia, including the map."));
        }

        if (Intents.Motion.Matches(q))
        {
            text.Add(copy.T("Hareketi azalt animasyonları kapatır; titreşimi de ayrıca kapatabilirsiniz.",
                "Reduce motion turns off animations; you can also turn off vibration separately."));
        }

        if (Intents.Language.Matches(q))
        {
            text.Add(copy.T("Uygulama dilini Türkçe veya İngilizce olarak değiştirebilirsiniz; eser bilgileri de seçtiğiniz dilde gelir.",
                "You can switch the app language between Turkish and English; monument information follows your choice."));
        }

        // Erişilebilirlik tercihlerinin tek yapılandırma noktası Erişilebilirlik sayfasıdır; tema ve
        // dil Ayarlar'dadır. Genel bir "ayarlar" sorusu ikisini de sunar.
        var accessibility = Intents.Accessibility.Matches(q);
        var settings = Intents.Theme.Matches(q) || Intents.Language.Matches(q) || !accessibility;
        text.Add(copy.T("Ayarlar'a ve Erişilebilirlik'e haritadaki hesap görselinden veya diğer sekmelerin başlığındaki hesap düğmesinden ulaşabilirsiniz.",
            "You can reach Settings and Accessibility from the account picture on the map or the account button in other tabs."));

        var suggestions = new List<string>();
        if (!Intents.TextSize.Matches(q))
        {
            suggestions.Add(copy.T("Yazıları nasıl büyütürüm?", "How do I make text larger?"));
        }

        if (!Intents.Theme.Matches(q))
        {
            suggestions.Add(copy.T("Koyu tema var mı?", "Is there a dark theme?"));
        }

        if (!Intents.ColorVision.Matches(q))
        {
            suggestions.Add(copy.T("Renk körlüğü desteği", "Colour blindness support"));
        }

        var actions = new List<GuideAction>();
        if (accessibility)
        {
            actions.Add(Section(GuideDestination.Accessibility));
        }

        if (settings)
        {
            actions.Add(Section(GuideDestination.Settings));
        }

        return new GuideAnswer(string.Join("\n\n", text), [], actions, suggestions.Take(2).ToArray());
    }

    private GuideAnswer MemoriesAnswer(Copy copy, GuideMonument? monument)
    {
        if (monument is null)
        {
            return new GuideAnswer(
                copy.T(
                    "Anı Köşesi her eserin sayfasındadır: ziyaretçiler fotoğraf ve notlarını paylaşır, anılar ziyaret tarihine göre bir zaman çizelgesinde sıralanır. Okumak herkese açıktır; paylaşmak için giriş yapmanız gerekir. Hangi eserin anılarını görmek istersiniz?",
                    "Every monument page has a Memory Corner where visitors share photos and notes, ordered on a timeline by visit date. Anyone can read them; you need to sign in to share. Which monument's memories would you like to see?"),
                [], [Search(GuideFilter.Empty, _monuments.Count)],
                _monuments.Take(2).Select(m => copy.T($"{m.Name.Tr} anıları", $"Memories of {m.Name.En}")).ToArray());
        }

        var name = monument.Name.Resolve(copy.Language);
        return new GuideAnswer(
            copy.T(
                $"{name} için paylaşılan anıları Anı Köşesi'nde, ziyaret tarihine göre sıralı bir zaman çizelgesinde görebilirsiniz. Kendi anınızı paylaşmak için giriş yapmanız gerekir.",
                $"Memories shared for {name} are in its Memory Corner, on a timeline ordered by visit date. Sign in to share your own."),
            [monument], [Monument("openMemories", copy, monument)],
            [copy.T("Kim yaptırdı?", "Who built it?"), copy.T("Nerede?", "Where is it?")], null, monument.Id);
    }

    private GuideAnswer RouteAnswer(Copy copy, GuideQuery q, GuideMonument? monument, GuideFilter filter)
    {
        if (Intents.Optimize.Matches(q))
        {
            return new GuideAnswer(
                copy.T(
                    "\"Sırayı optimize et\" ilk durağı başlangıç olarak sabit tutar ve diğer durakları toplam kuş uçuşu mesafe en kısa olacak şekilde yeniden sıralar. Kaydetmeden önce sonucu görür, durakları sürükleyerek yine değiştirebilirsiniz.",
                    "\"Optimise order\" keeps the first stop as the start and reorders the others for the shortest straight-line distance. You see the result before saving and can still drag stops around."),
                [], [Section(GuideDestination.RouteBuilder)],
                [copy.T("Rota süresi nasıl hesaplanır?", "How is the route duration calculated?")]);
        }

        var intro = copy.T(
            "Rota planlayıcıyla 2–10 eserlik bir gezi rotası oluşturabilirsiniz. Eserleri seçip sırayı optimize edebilirsiniz; süre karayolu yolculuğu, ziyaret ve mola süreleri dahil hesaplanır. Rotalarınız giriş yapmadan da cihazınızda saklanır ve PDF olarak paylaşılabilir.",
            "The route planner builds a trip of 2–10 monuments. Pick monuments and optimise the order; the duration includes road travel, visit and break times. Routes are kept on your device without signing in and can be shared as PDF.");
        if (monument is not null)
        {
            var name = monument.Name.Resolve(copy.Language);
            return new GuideAnswer(
                string.Join("\n\n", intro, copy.T($"{name} eserini eklemek için eser sayfasındaki \"Rotaya ekle\" butonunu kullanın.",
                    $"To add {name}, use \"Add to route\" on its page.")),
                [monument], [Monument("openMonument", copy, monument), Section(GuideDestination.RouteBuilder)], [], null, monument.Id);
        }

        var matches = filter.IsEmpty ? [] : filter.Apply(_monuments);
        var text = new List<string> { intro };
        if (!filter.IsEmpty)
        {
            text.Add(matches.Count == 0
                ? copy.T($"Katalogda bu ölçütlere uyan eser bulamadım ({filter.Describe(copy)}).",
                    $"I found no monuments matching these criteria ({filter.Describe(copy)}).")
                : $"{copy.T("Rotanız için seçebileceğiniz eserler", "Monuments you could add")} ({filter.Describe(copy)}):\n{Bullets(copy, matches)}");
        }

        return new GuideAnswer(string.Join("\n\n", text), matches.Take(MaxListed).ToArray(),
            [Section(GuideDestination.RouteBuilder), matches.Count > 0 ? Search(filter, matches.Count) : Section(GuideDestination.RoutePlanner)],
            [copy.T("Sırayı optimize et ne yapar?", "What does optimise order do?"),
                copy.T("Konya'daki Selçuklu eserleri", "Seljuk monuments in Konya")]);
    }

    private static GuideAnswer AmbiguousAnswer(Copy copy, IReadOnlyList<GuideMonument> matches) => new(
        copy.T($"Birden fazla eser buldum. Hangisini kastettiniz?\n{Bullets(copy, matches)}",
            $"I found more than one monument. Which one do you mean?\n{Bullets(copy, matches)}"),
        matches.Take(MaxListed).ToArray(),
        matches.Take(3).Select(m => (GuideAction)Monument("openMonument", copy, m)).ToArray(),
        matches.Take(MaxSuggestions).Select(m => m.Name.Resolve(copy.Language)).ToArray());

    private GuideAnswer MonumentAnswer(Copy copy, GuideMonument monument, Facet? facet)
    {
        var language = copy.Language;
        var name = monument.Name.Resolve(language);
        var place = $"{monument.District}, {monument.City}";
        var architect = monument.Architect?.Resolve(language);
        var founderLine = $"{copy.T("Banisi (yaptıran)", "Founder")}: {monument.Founder.Resolve(language)}";
        var dateLine = $"{copy.T("Yapım tarihi", "Built")}: {monument.ConstructionDate.Resolve(language)}";
        var architectLine = architect is null
            ? copy.T("Katalogda mimar bilgisi bulunmuyor.", "The catalogue has no architect on record.")
            : $"{copy.T("Mimarı", "Architect")}: {architect}";
        IEnumerable<string> body = facet switch
        {
            Facet.Founder => [founderLine, dateLine],
            Facet.Architect => [architectLine, founderLine],
            Facet.Date => [dateLine, founderLine],
            Facet.Location => [$"{copy.T("Konum", "Location")}: {place}", $"{copy.T("Adres", "Address")}: {monument.Address}"],
            Facet.Period => [$"{copy.T("Dönem", "Period")}: {copy.Period(monument.Period)}", dateLine],
            Facet.Unesco =>
            [
                monument.IsUnescoWorldHeritage
                    ? copy.T($"{name}, UNESCO Dünya Mirası Listesi'nde yer alır.", $"{name} is on the UNESCO World Heritage List.")
                    : copy.T($"{name}, UNESCO Dünya Mirası Listesi'nde yer almıyor.", $"{name} is not on the UNESCO World Heritage List."),
            ],
            Facet.History => [FirstSentences(monument.History.Resolve(language))],
            _ => new[] { monument.ShortDescription.Resolve(language), dateLine, founderLine }
                .Concat(architect is null ? [] : [architectLine]),
        };
        var others = _monuments.Count(m => m.City == monument.City && m.Id != monument.Id);
        GuideAction[] actions = facet == Facet.Location
            ? [Monument("showOnMap", copy, monument), Monument("openMonument", copy, monument), Monument("openMemories", copy, monument)]
            : [Monument("openMonument", copy, monument), Monument("showOnMap", copy, monument), Monument("openMemories", copy, monument)];

        var suggestions = new List<string>();
        if (facet != Facet.Founder)
        {
            suggestions.Add(copy.T("Kim yaptırdı?", "Who built it?"));
        }

        if (facet != Facet.Architect)
        {
            suggestions.Add(copy.T("Mimarı kim?", "Who was the architect?"));
        }

        if (facet is not null && facet != Facet.Date)
        {
            suggestions.Add(copy.T("Ne zaman yapıldı?", "When was it built?"));
        }

        if (facet != Facet.History)
        {
            suggestions.Add(copy.T("Tarihçesini anlat", "Tell me its history"));
        }

        if (others > 0)
        {
            suggestions.Add(copy.T($"{monument.City} ilindeki diğer eserler", $"Other monuments in {monument.City}"));
        }

        return new GuideAnswer(string.Join('\n', new[] { $"{name} ({place})" }.Concat(body)), [monument], actions,
            suggestions.Take(3).ToArray(), null, monument.Id);
    }

    private GuideAnswer ListAnswer(Copy copy, GuideFilter filter, PeopleMatch people, string? excludeId)
    {
        var byPerson = people.Monuments.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        var matches = filter.Apply(_monuments).Where(m => m.Id != excludeId && (byPerson.Count == 0 || byPerson.Contains(m.Id))).ToArray();
        var criteria = string.Join(" · ", people.Names.Concat(filter.IsEmpty ? [] : [filter.Describe(copy)]));
        if (matches.Length == 0)
        {
            return new GuideAnswer(
                copy.T($"Katalogda bu ölçütlere uyan eser bulamadım ({criteria}). Farklı bir il, yapı türü veya dönem deneyebilirsiniz.",
                    $"I found no monuments in the catalogue matching these criteria ({criteria}). Try another province, type or period."),
                [], [Search(GuideFilter.Empty, _monuments.Count)],
                Cities.Take(2).Select(city => copy.T($"{city} ilindeki eserler", $"Monuments in {city}")).ToArray());
        }

        var single = matches.Length == 1 ? matches[0] : null;
        // Kişi ölçütü arama filtresinde yoktur; o durumda eserler tek tek açılır.
        var searchable = people.Monuments.Count == 0 && !filter.Unesco;
        var actions = new List<GuideAction>();
        if (single is not null)
        {
            actions.Add(Monument("openMonument", copy, single));
            actions.Add(Monument("showOnMap", copy, single));
        }
        else if (searchable)
        {
            actions.Add(Search(filter, matches.Length));
        }
        else
        {
            actions.AddRange(matches.Take(3).Select(m => Monument("openMonument", copy, m)));
        }

        actions.Add(Section(GuideDestination.RouteBuilder));
        return new GuideAnswer(
            copy.T($"{criteria} için katalogda {matches.Length} eser buldum:\n{Bullets(copy, matches)}",
                $"I found {matches.Length} monuments in the catalogue for {criteria}:\n{Bullets(copy, matches)}"),
            matches.Take(MaxListed).ToArray(), actions,
            matches.Take(2).Select(m => copy.T($"{m.Name.Tr} hakkında bilgi", $"About {m.Name.En}")).ToArray(), null, single?.Id);
    }

    private GuideAnswer OverviewAnswer(Copy copy) => new(
        copy.T(
            $"Katalogda şu anda {_monuments.Count} vakıf eseri var. İller: {string.Join(", ", Cities)}. İl, yapı türü (cami, han, külliye…) veya döneme (Selçuklu, Beylikler, Osmanlı) göre sorabilirsiniz.",
            $"The catalogue currently has {_monuments.Count} foundation monuments. Provinces: {string.Join(", ", Cities)}. You can ask by province, type (mosque, inn, complex…) or period (Seljuk, Beylik, Ottoman)."),
        [], [Search(GuideFilter.Empty, _monuments.Count), Section(GuideDestination.Map)],
        [copy.T("İstanbul'daki camiler", "Mosques in İstanbul"), copy.T("UNESCO listesindeki eserler", "UNESCO-listed monuments")]);

    private static GuideAnswer MapAnswer(Copy copy) => new(
        copy.T(
            "Haritada tüm vakıf eserlerini görebilirsiniz. Yakınınızdaki eserler için haritadaki konum butonuna dokunun; konumunuz yalnızca cihazınızda kullanılır, Rehber'e veya sunucuya gönderilmez.",
            "The map shows all foundation monuments. For monuments near you, tap the location button on the map; your location is used only on your device and is never sent to the Guide or the server."),
        [], [Section(GuideDestination.Map)], [copy.T("Eserleri listele", "List the monuments")]);

    private static GuideAnswer HelpAnswer(Copy copy) => new(
        copy.T(
            "Size şu konularda yardımcı olabilirim:\n• Bir eser hakkında bilgi (bani, mimar, tarih, konum)\n• İle, yapı türüne veya döneme göre eser bulma\n• Rota planlama\n• Anıları görme ve paylaşma\n• Hasar bildirme\n• Tema, metin boyutu ve erişilebilirlik ayarları",
            "I can help you with:\n• Information about a monument (founder, architect, date, location)\n• Finding monuments by province, type or period\n• Planning a route\n• Reading and sharing memories\n• Reporting damage\n• Theme, text size and accessibility settings"),
        [], [], copy.Starters);

    private GuideAnswer FallbackAnswer(Copy copy) => new(
        copy.T(
            "Bu soruyu yanıtlayamadım. Vakıf eserleri, rota planlama, Anı Köşesi, hasar bildirimi ve uygulama ayarları hakkında yardımcı olabilirim. Sorunuzu bir eser, il veya yapı türüyle yeniden yazabilirsiniz.",
            "I couldn't answer that. I can help with foundation monuments, route planning, the Memory Corner, damage reports and app settings. Try rephrasing with a monument, province or building type."),
        [], [Search(GuideFilter.Empty, _monuments.Count)], copy.Starters);

    // ── Eylemler ─────────────────────────────────────────────────────────

    private static MonumentAction Monument(string type, Copy copy, GuideMonument monument) =>
        new(type, monument.Id, monument.Name.Resolve(copy.Language));

    private static GuideAction ReportDamage(Copy copy, GuideMonument? monument) =>
        monument is null ? new ReportDamageAction() : Monument("reportDamage", copy, monument);

    private static SectionAction Section(GuideDestination destination) =>
        new(JsonNaming(destination.ToString()));

    private static SearchAction Search(GuideFilter filter, int total) => new(filter.Categories, filter.Periods, filter.Cities, total);

    private static string JsonNaming(string name) => char.ToLowerInvariant(name[0]) + name[1..];

    private static GuideMonument[] Sources(GuideMonument? monument) => monument is null ? [] : [monument];

    // ── Arama (retrieval) ────────────────────────────────────────────────

    /// <summary>
    /// Adı soruda geçen eserler; eşit puanlılarda il/ilçe, sonra yapı türü, en son adın soruda geçen payı
    /// öne geçer ("Süleymaniye Camii" külliyeyi değil camiyi; "Selimiye Camii" adı tam eşleşen camiyi,
    /// "Sultan Selim (Selimiye) Camii"ni değil bulur). Eşleşen sözcüklerinin tümü kişi adı olan eser
    /// ("Mimar Sinan Türbesi" ← "Mimar Sinan'ın eserleri") aday değildir.
    /// </summary>
    private List<GuideMonument> MatchNames(GuideQuery q)
    {
        var scores = new List<(GuideMonument Monument, double Score)>();
        foreach (var monument in _monuments)
        {
            var matched = _nameTerms[monument.Id].Where(q.MatchesTerm).ToList();
            // Önek eşleşmesi: "selimiye" sözcüğü hem "selim" hem "selimiye" terimini tutar; bir kez sayılır.
            matched = matched.Where(term => !matched.Any(other =>
                other.Length > term.Length && other.StartsWith(term, StringComparison.Ordinal))).ToList();
            if (matched.Count == 0 || matched.All(_personVocabulary.Contains))
            {
                continue;
            }

            var place = q.MatchesTerm(TurkishText.Fold(monument.City)) || q.MatchesTerm(TurkishText.Fold(monument.District));
            var type = _typeTerms[monument.Id].Any(q.MatchesTerm);
            var coverage = (double)matched.Count / _nameTerms[monument.Id].Count;
            scores.Add((monument, matched.Count + (place ? 0.5 : 0) + (type ? 0.125 : 0) + (coverage * 0.0625)));
        }

        if (scores.Count == 0)
        {
            return [];
        }

        var best = scores.Max(s => s.Score);
        return scores.Where(s => s.Score == best).Select(s => s.Monument).ToList();
    }

    private sealed record PeopleMatch(IReadOnlyList<GuideMonument> Monuments, IReadOnlyList<string> Names);

    /// <summary>Banisi veya mimarı soruda geçen eserler; en çok sözcüğü eşleşen kişiler kazanır.</summary>
    private PeopleMatch MatchPeople(GuideQuery q, string language)
    {
        var scored = new List<(GuideMonument Monument, Text Person, int Score)>();
        foreach (var monument in _monuments)
        {
            foreach (var (person, terms) in _personTerms[monument.Id])
            {
                var score = terms.Count(q.MatchesTerm);
                if (score > 0)
                {
                    scored.Add((monument, person, score));
                }
            }
        }

        if (scored.Count == 0)
        {
            return new PeopleMatch([], []);
        }

        var best = scored.Max(s => s.Score);
        var monuments = new List<GuideMonument>();
        var names = new List<string>();
        foreach (var (monument, person, score) in scored.Where(s => s.Score == best))
        {
            var name = person.Resolve(language);
            if (!names.Contains(name))
            {
                names.Add(name);
            }

            if (monuments.Count == 0 || monuments[^1] != monument)
            {
                monuments.Add(monument);
            }
        }

        return new PeopleMatch(monuments, names);
    }

    private IEnumerable<string> Cities => _monuments.Select(m => m.City).Distinct(StringComparer.Ordinal).OrderBy(c => c, TurkishText.Comparer);

    private static string Bullets(Copy copy, IReadOnlyList<GuideMonument> monuments)
    {
        var lines = monuments.Take(MaxListed).Select(m => $"• {m.Name.Resolve(copy.Language)} ({m.City})").ToList();
        var rest = monuments.Count - MaxListed;
        if (rest > 0)
        {
            lines.Add(copy.T($"…ve {rest} eser daha", $"…and {rest} more"));
        }

        return string.Join('\n', lines);
    }

    private static string FirstSentences(string text, int count = 2)
    {
        var sentences = Sentence().Matches(text);
        return sentences.Count <= count
            ? text.Trim()
            : string.Join(' ', sentences.Take(count).Select(m => m.Value.Trim()));
    }

    [GeneratedRegex("[^.!?]+[.!?]+")]
    private static partial Regex Sentence();
}
