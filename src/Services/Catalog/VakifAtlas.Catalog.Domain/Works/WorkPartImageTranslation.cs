namespace VakifAtlas.Catalog.Domain.Works;

public class WorkPartImageTranslation
{
    public long PartImageId { get; set; }
    public string Locale { get; set; } = null!;
    public string? AltText { get; set; }
    public string? Title { get; set; }
}
