using VakifAtlas.Catalog.Domain.References;

namespace VakifAtlas.Catalog.Domain.Complexes;

public class ComplexImage
{
    public const int MaxCount = 20;

    public long ComplexId { get; set; }
    public short Position { get; set; } // 0 = cover
    public long MediaId { get; set; }
    public MediaAsset Media { get; set; } = null!;
}
