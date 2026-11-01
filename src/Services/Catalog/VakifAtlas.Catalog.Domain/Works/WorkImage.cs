using VakifAtlas.Catalog.Domain.References;

namespace VakifAtlas.Catalog.Domain.Works;

public class WorkImage
{
    public long Id { get; set; }
    public long WorkId { get; set; }
    public int Position { get; set; } // 0 = cover
    public long MediaId { get; set; }
    public MediaAsset Media { get; set; } = null!;

    public List<WorkImageTranslation> Translations { get; set; } = [];
}
