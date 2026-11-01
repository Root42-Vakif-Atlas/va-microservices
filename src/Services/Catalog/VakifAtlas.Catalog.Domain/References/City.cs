namespace VakifAtlas.Catalog.Domain.References;

public class City
{
    public short Code { get; set; } // plate code
    public string Name { get; set; } = null!;
    public bool IsActive { get; set; } = true;
}
