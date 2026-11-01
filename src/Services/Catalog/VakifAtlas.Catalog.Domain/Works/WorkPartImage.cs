using VakifAtlas.Catalog.Domain.References;

namespace VakifAtlas.Catalog.Domain.Works;

public class WorkPartImage
{
    public long Id { get; set; }
    public long PartId { get; set; }
    public int Position { get; set; }
    public long MediaId { get; set; }
    public MediaAsset Media { get; set; } = null!;

    public List<WorkPartImageTranslation> Translations { get; set; } = [];
}
