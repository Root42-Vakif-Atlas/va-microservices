namespace VakifAtlas.Catalog.Domain.Complexes;

public class ComplexTranslation
{
    public long ComplexId { get; set; }
    public string Locale { get; set; } = null!;
    public string Name { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
