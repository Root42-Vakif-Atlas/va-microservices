namespace VakifAtlas.Catalog.Domain.Works;

public class WorkImageTranslation
{
    public long WorkImageId { get; set; }
    public string Locale { get; set; } = null!;
    public string? AltText { get; set; }
    public string? Title { get; set; }
}
