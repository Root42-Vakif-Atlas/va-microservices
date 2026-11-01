namespace VakifAtlas.Catalog.Domain.Works;

public class WorkTranslation
{
    public long WorkId { get; set; }
    public string Locale { get; set; } = null!;
    public string? Slug { get; set; } // non-tr only; tr URL uses Work.Key
    public string? Title { get; set; } // required for tr (checked in importer)
    public string? Nickname { get; set; }
    public string? ShortDescriptionTitle { get; set; }
    public string? ShortDescription { get; set; }

    // staff-entered (not in the bulk file); imports leave them as they are
    public string? History { get; set; } // unbounded
    public string? ConstructionDate { get; set; }
    public string? Founder { get; set; }
    public string? Architect { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
