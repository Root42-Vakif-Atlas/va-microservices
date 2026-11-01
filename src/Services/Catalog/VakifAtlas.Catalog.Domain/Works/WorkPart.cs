namespace VakifAtlas.Catalog.Domain.Works;

// a section of a work
public class WorkPart
{
    public long Id { get; set; }
    public long WorkId { get; set; }
    public int Position { get; set; }

    public List<WorkPartTranslation> Translations { get; set; } = [];
    public List<WorkPartImage> Images { get; set; } = [];
}
