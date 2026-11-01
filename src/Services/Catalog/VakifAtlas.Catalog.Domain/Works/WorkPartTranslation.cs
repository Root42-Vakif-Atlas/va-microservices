namespace VakifAtlas.Catalog.Domain.Works;

public class WorkPartTranslation
{
    public long PartId { get; set; }
    public string Locale { get; set; } = null!;
    public string? Title { get; set; }
    public string? Description { get; set; } // unbounded
}
