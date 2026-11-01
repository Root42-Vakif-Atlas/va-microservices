using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using VakifAtlas.Catalog.Api.Infrastructure.Persistence;
using VakifAtlas.Persistence;

namespace VakifAtlas.Catalog.Api.Infrastructure.Import;

public sealed class CatalogSeedOptions
{
    public const string Section = "Catalog:Seed";

    // folder searched for */root42-veri.json (relative to the content root)
    public string Path { get; set; } = "seed";
}

// imports a root42 file from disk (CLI `import <file>`, seeder): images are read next to the file, hashed,
// stored once, then the whole file goes through the importer (membership included)
public sealed class CatalogFileImport(
    CatalogDbContext db,
    CatalogImporter importer,
    ImportImageStore images,
    ILogger<CatalogFileImport> logger)
{
    private const int ReportedIssues = 20;

    public async Task<ImportResult> ImportFileAsync(string path, CancellationToken cancellationToken)
    {
        var (file, parseError) = VgmValidator.Parse(await File.ReadAllTextAsync(path, cancellationToken));
        if (file is null)
        {
            throw new InvalidDataException($"{path}: {Describe([parseError!])}");
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var src in VgmValidator.ImageSources(file))
        {
            // src is relative to the file (schema); a missing file is reported by the validator
            var image = Path.GetFullPath(Path.Combine(directory, src));
            if (!image.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(image))
            {
                continue;
            }

            await using var stream = File.OpenRead(image);
            files[src] = image;
            hashes[src] = await ImportImageStore.HashAsync(stream, cancellationToken);
        }

        var cities = (await db.Cities.Select(c => c.Code).ToListAsync(cancellationToken)).ToHashSet();
        var validation = VgmValidator.Validate(file, cities, hashes.ContainsKey);
        var errors = validation.Errors.Count > 0 ? validation.Errors : await importer.CheckAgainstDatabaseAsync(file, cancellationToken);
        if (errors.Count > 0)
        {
            throw new InvalidDataException($"{path}: {errors.Count} errors: {Describe(errors)}");
        }

        if (validation.Warnings.Count > 0)
        {
            logger.LogWarning("{File}: {Count} warnings: {Warnings}", path, validation.Warnings.Count, Describe(validation.Warnings));
        }

        var stored = await images.FindAsync(hashes.Values, cancellationToken);
        // lazy filter: a file seen under another src earlier in this loop is skipped too
        foreach (var (src, sha) in hashes.Where(h => !stored.ContainsKey(h.Value)))
        {
            await using var stream = File.OpenRead(files[src]);
            var result = await images.StoreAsync(sha, stream, src, cancellationToken);
            if (!result.IsSuccess)
            {
                throw new InvalidDataException($"{path}: image {src} was rejected.");
            }

            stored[sha] = result.Value.Image.MediaId;
        }

        var media = hashes.ToDictionary(h => h.Key, h => stored[h.Value], StringComparer.Ordinal);
        return await importer.ImportFileAsync(file, media, cancellationToken);
    }

    private static string Describe(IEnumerable<VgmIssue> issues) =>
        string.Join("; ", issues.Take(ReportedIssues).Select(i => i.Detail is null ? $"{i.Path} {i.Code}" : $"{i.Path} {i.Code} ({i.Detail})"));
}

// dev/staging: imports every seed file once. A file whose works already exist is skipped, so staff edits and
// archives are not overwritten on startup
internal sealed class CatalogSeeder(
    CatalogDbContext db,
    CatalogFileImport import,
    IHostEnvironment environment,
    IOptions<CatalogSeedOptions> options,
    ILogger<CatalogSeeder> logger) : IDataSeeder
{
    public const string FileName = "root42-veri.json";

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(Path.Combine(environment.ContentRootPath, options.Value.Path));
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(root, FileName, SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var (file, _) = VgmValidator.Parse(await File.ReadAllTextAsync(path, cancellationToken));
            var keys = file?.Works?.Select(w => w.Key).OfType<string>().ToList() ?? [];
            if (keys.Count == 0 || await db.Works.AnyAsync(w => keys.Contains(w.Key), cancellationToken))
            {
                continue;
            }

            var result = await import.ImportFileAsync(path, cancellationToken);
            logger.LogInformation("Seeded {File}: {Works} works, {Complexes} complexes", path, result.CreatedWorks,
                result.CreatedComplexes);
        }
    }
}
