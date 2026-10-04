using ConeXion.Core.Models;

namespace ConeXion.Core.Services;

public sealed class CatalogService
{
    public const int SupportedSchemaVersion = 2;
    private readonly AppPaths _paths;
    public CatalogService(AppPaths paths) => _paths = paths;

    public bool Exists => File.Exists(_paths.CatalogPackage);

    public CatalogDocument LoadCurrent()
    {
        if (!Exists)
            throw new FileNotFoundException("No existe un catálogo local válido. ConeXion intentará recuperarlo desde Supabase cuando exista conexión.");
        return Validate(CatalogCodec.Decode(File.ReadAllBytes(_paths.CatalogPackage)));
    }

    public CatalogDocument Validate(CatalogDocument catalog)
    {
        if (catalog.SchemaVersion != SupportedSchemaVersion)
            throw new InvalidDataException($"Schema de catálogo no compatible: {catalog.SchemaVersion}. ConeXion requiere schema {SupportedSchemaVersion}.");
        if (catalog.CatalogVersion <= 0)
            throw new InvalidDataException("Versión de catálogo inválida.");
        if (!DateTimeOffset.TryParse(catalog.GeneratedAt, out _))
            throw new InvalidDataException("generated_at del catálogo no es válido.");

        foreach (var p in catalog.Productos)
        {
            if (p.CInterno <= 20000) throw new InvalidDataException($"C. interno inválido en productos[]: {p.CInterno}.");
            if (string.IsNullOrWhiteSpace(p.Producto)) throw new InvalidDataException($"Producto vacío para C. interno {p.CInterno}.");
            if (p.Precio < 0) throw new InvalidDataException($"Precio negativo para C. interno {p.CInterno}.");
        }

        foreach (var p in catalog.Excluidos)
        {
            if (p.CInterno <= 20000) throw new InvalidDataException($"C. interno inválido en excluidos[]: {p.CInterno}.");
            if (string.IsNullOrWhiteSpace(p.Producto)) throw new InvalidDataException($"Producto excluido vacío para C. interno {p.CInterno}.");
        }

        var productDupes = catalog.Productos.GroupBy(x => x.CInterno).Where(g => g.Count() > 1).Select(g => g.Key).Take(10).ToArray();
        if (productDupes.Length > 0)
            throw new InvalidDataException($"Catálogo con C. internos duplicados en productos[]: {string.Join(", ", productDupes)}.");

        var excludedDupes = catalog.Excluidos.GroupBy(x => x.CInterno).Where(g => g.Count() > 1).Select(g => g.Key).Take(10).ToArray();
        if (excludedDupes.Length > 0)
            throw new InvalidDataException($"Catálogo con C. internos duplicados en excluidos[]: {string.Join(", ", excludedDupes)}.");

        var productCodes = catalog.Productos.Select(x => x.CInterno).ToHashSet();
        var intersection = catalog.Excluidos.Select(x => x.CInterno).Where(productCodes.Contains).Take(10).ToArray();
        if (intersection.Length > 0)
            throw new InvalidDataException($"SKU presentes simultáneamente en productos[] y excluidos[]: {string.Join(", ", intersection)}.");

        return catalog;
    }

    public (CatalogDocument Catalog, string PackageSha256) ValidatePackage(byte[] package, string? expectedSha256 = null, long? expectedSize = null)
    {
        if (package.Length == 0) throw new InvalidDataException("Supabase devolvió un catálogo vacío.");
        if (expectedSize is > 0 && package.LongLength != expectedSize.Value)
            throw new InvalidDataException($"El tamaño del catálogo no coincide. Esperado: {expectedSize.Value}; recibido: {package.LongLength}.");

        var packageHash = HashService.Sha256Bytes(package);
        if (!string.IsNullOrWhiteSpace(expectedSha256) && !string.Equals(packageHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SHA-256 del catálogo no coincide con Supabase.");

        return (Validate(CatalogCodec.Decode(package)), packageHash);
    }

    public void InstallPackageAtomically(byte[] package, string expectedSha256, long expectedSize, int expectedVersion, int expectedSchemaVersion)
    {
        _paths.EnsureSharedDirectories();
        if (expectedVersion <= 0) throw new InvalidDataException("Supabase devolvió una versión de catálogo inválida.");
        if (expectedSchemaVersion != SupportedSchemaVersion)
            throw new InvalidDataException($"Schema de catálogo no compatible: {expectedSchemaVersion}. ConeXion requiere schema {SupportedSchemaVersion}.");
        if (expectedSha256.Length != 64 || !expectedSha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Supabase devolvió un SHA-256 de catálogo inválido.");
        if (expectedSize <= 0) throw new InvalidDataException("Supabase devolvió un tamaño de catálogo inválido.");

        var validated = ValidatePackage(package, expectedSha256, expectedSize);
        if (validated.Catalog.CatalogVersion != expectedVersion)
            throw new InvalidDataException($"La versión interna del catálogo ({validated.Catalog.CatalogVersion}) no coincide con Supabase ({expectedVersion}).");
        if (validated.Catalog.SchemaVersion != expectedSchemaVersion)
            throw new InvalidDataException($"El schema interno del catálogo ({validated.Catalog.SchemaVersion}) no coincide con Supabase ({expectedSchemaVersion}).");

        var temp = _paths.CatalogPackage + ".tmp";
        if (File.Exists(temp)) File.Delete(temp);
        File.WriteAllBytes(temp, package);

        var movedCurrentToPrevious = false;
        try
        {
            if (File.Exists(_paths.PreviousCatalogPackage)) File.Delete(_paths.PreviousCatalogPackage);
            if (File.Exists(_paths.CatalogPackage))
            {
                File.Move(_paths.CatalogPackage, _paths.PreviousCatalogPackage);
                movedCurrentToPrevious = true;
            }
            File.Move(temp, _paths.CatalogPackage);
        }
        catch
        {
            if (File.Exists(temp)) File.Delete(temp);
            if (!File.Exists(_paths.CatalogPackage) && movedCurrentToPrevious && File.Exists(_paths.PreviousCatalogPackage))
                File.Move(_paths.PreviousCatalogPackage, _paths.CatalogPackage);
            throw;
        }
    }

    public void RollbackUnconfirmedInstall()
    {
        if (File.Exists(_paths.CatalogPackage)) File.Delete(_paths.CatalogPackage);
        if (File.Exists(_paths.PreviousCatalogPackage))
            File.Move(_paths.PreviousCatalogPackage, _paths.CatalogPackage);
    }

    public string CurrentPackageSha256() => Exists ? HashService.Sha256File(_paths.CatalogPackage) : "";
}
