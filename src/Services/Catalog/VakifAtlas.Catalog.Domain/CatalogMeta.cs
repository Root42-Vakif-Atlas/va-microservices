namespace VakifAtlas.Catalog.Domain;

// single row: catalog version, bumped in the transaction of every write. Map tile URLs carry it (v) so CDN
// copies of an older catalog are never served for a newer one
public class CatalogMeta
{
    public const short SingletonId = 1;

    public short Id { get; set; } = SingletonId;
    public long Version { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
