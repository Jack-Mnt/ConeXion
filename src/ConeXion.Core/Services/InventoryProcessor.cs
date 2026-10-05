using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ConeXion.Core.Models;

namespace ConeXion.Core.Services;

public sealed class InventoryProcessor
{
    private static readonly TimeSpan PeruOffset = TimeSpan.FromHours(-5);

    public ProcessingResult Process(string excelPath, CatalogDocument catalog, string expectedStore, IProgress<string>? progress = null)
    {
        progress?.Report("Validando archivo...");
        ValidateFile(excelPath);
        var rows = XlsxReader.ReadProducts(excelPath);

        var resolvedStores = rows
            .Select(r => ResolveStoreFromWarehouse(r.WarehouseName))
            .Where(s => s is not null)
            .Select(s => s!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var unresolvedWarehouses = rows
            .Select(r => r.WarehouseName)
            .Where(w => !string.IsNullOrWhiteSpace(w) && ResolveStoreFromWarehouse(w) is null)
            .Select(NormalizeWarehouse)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToArray();

        if (resolvedStores.Length == 0)
        {
            var detail = unresolvedWarehouses.Length > 0 ? $" Valores detectados: {string.Join(", ", unresolvedWarehouses)}." : "";
            throw new InvalidDataException($"No se pudo reconocer la sede desde la columna Nombre de Almacén.{detail}");
        }
        if (resolvedStores.Length != 1)
            throw new InvalidDataException($"El archivo contiene más de una sede reconocible en Nombre de Almacén: {string.Join(", ", resolvedStores)}.");

        var storeDisplay = resolvedStores[0];
        if (!string.Equals(NormalizeStoreName(expectedStore), NormalizeStoreName(storeDisplay), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"El archivo pertenece a la sede {storeDisplay}. Esta computadora está configurada para {expectedStore}.");

        progress?.Report("Comparando catálogo...");
        var catalogByCode = catalog.Productos.ToDictionary(p => p.CInterno);
        var excludedCodes = catalog.Excluidos.Select(p => p.CInterno).ToHashSet();
        var incidents = new List<Incident>();
        var stock = new List<StockItem>();
        var deleted = new Dictionary<int, DeletedProduct>();

        var parsed = rows.Select(r => new ParsedRow(r, r.RowNumber, ParseCode(r.InternalCodeRaw))).ToList();
        var skuTotalExcel = parsed.Count(x => !IsAuxiliaryCode(x.Code));

        var duplicateGroups = parsed
            .Where(x => x.Code is > 20000)
            .GroupBy(x => x.Code!.Value)
            .Where(g => g.Count() > 1)
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.RowNumber).ToArray());

        // Excluidos quedan completamente fuera del flujo operativo, incluso si se repiten.
        // Duplicados conocidos se representan como falta de observación válida.
        foreach (var (code, duplicatedRows) in duplicateGroups.OrderBy(x => x.Key))
        {
            if (excludedCodes.Contains(code)) continue;
            if (!catalogByCode.ContainsKey(code))
                throw new InvalidDataException($"El producto nuevo con C. interno {code} aparece más de una vez. El contrato V2 no permite elegir arbitrariamente una observación para un producto nuevo duplicado.");

            deleted[code] = new DeletedProduct { CInterno = code, Motivo = "codigo_interno_duplicado" };
            incidents.Add(NewIncident(code, "codigo_interno_duplicado",
                ("filas", duplicatedRows.Select(x => x.RowNumber).ToArray()),
                ("valores_stock", duplicatedRows.Select(x => NullIfBlank(x.Row.StockRaw)).ToArray()),
                ("producto", duplicatedRows.Select(x => NullIfBlank(x.Row.ProductName)).FirstOrDefault(x => x is not null))));
        }

        var seenKnownCodes = new HashSet<int>();

        foreach (var x in parsed)
        {
            var r = x.Row;

            if (IsAuxiliaryCode(x.Code))
                continue;

            if (x.Code is null || x.Code <= 20000)
            {
                incidents.Add(new Incident
                {
                    CInterno = null,
                    CInternoOriginal = string.IsNullOrWhiteSpace(r.InternalCodeRaw) ? null : r.InternalCodeRaw.Trim(),
                    Tipo = "codigo_interno_invalido",
                    Datos = CompactData(("fila", x.RowNumber), ("producto", NullIfBlank(r.ProductName)))
                });
                continue;
            }

            var code = x.Code.Value;

            if (excludedCodes.Contains(code))
                continue;

            var isKnown = catalogByCode.TryGetValue(code, out var cp);
            if (isKnown) seenKnownCodes.Add(code);

            if (duplicateGroups.ContainsKey(code))
                continue;

            var name = r.ProductName.Trim();
            var barcode = NormalizeBarcode(r.Barcode);
            var hasPrice = TryParsePrice(r.PriceRaw, out var price);
            var hasStock = TryParseWholeNumber(r.StockRaw, out var qty);

            if (!isKnown)
            {
                if (!hasStock)
                    throw new InvalidDataException($"El producto nuevo con C. interno {code} tiene stock inválido en la fila {x.RowNumber}; no puede construirse una incidencia producto_nuevo válida.");
                if (!hasPrice)
                    throw new InvalidDataException($"El producto nuevo con C. interno {code} tiene precio inválido en la fila {x.RowNumber}; no puede construirse una incidencia producto_nuevo válida.");
                if (price < 0)
                    throw new InvalidDataException($"El producto nuevo con C. interno {code} tiene precio negativo en la fila {x.RowNumber}; no puede construirse una incidencia producto_nuevo válida.");
                if (string.IsNullOrWhiteSpace(name))
                    throw new InvalidDataException($"El producto nuevo con C. interno {code} no tiene nombre en la fila {x.RowNumber}.");

                incidents.Add(NewIncident(code, "producto_nuevo",
                    ("producto", name),
                    ("c_barras", NullIfBlank(barcode)),
                    ("precio", price),
                    ("stock", qty)));
                continue;
            }

            // Los cambios comerciales pueden detectarse aunque el stock de este SKU sea inválido.
            Compare(cp!, name, barcode, hasPrice ? price : null, x.RowNumber, incidents);

            if (!hasStock)
            {
                deleted[code] = new DeletedProduct { CInterno = code, Motivo = "stock_invalido" };
                incidents.Add(NewIncident(code, "stock_invalido",
                    ("fila", x.RowNumber),
                    ("producto", NullIfBlank(r.ProductName)),
                    ("valor_original", NullIfBlank(r.StockRaw))));
                continue;
            }

            if (qty != 0)
                stock.Add(new StockItem { CInterno = code, Stock = qty });
        }

        // Un SKU incluido que no apareció en ninguna fila con identidad válida se reporta como producto_ausente.
        foreach (var p in catalog.Productos.Where(p => !seenKnownCodes.Contains(p.CInterno)).OrderBy(p => p.CInterno))
        {
            deleted[p.CInterno] = new DeletedProduct { CInterno = p.CInterno, Motivo = "producto_ausente" };
            incidents.Add(NewIncident(p.CInterno, "producto_ausente", ("producto", p.Producto)));
        }

        // Seguridad local: stock[] y eliminados[] deben ser disjuntos y stock[] no puede repetir SKU.
        var stockDupes = stock.GroupBy(x => x.CInterno).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
        if (stockDupes.Length > 0)
            throw new InvalidDataException($"Se generó stock duplicado para: {string.Join(", ", stockDupes)}.");
        var conflicts = stock.Select(x => x.CInterno).Where(deleted.ContainsKey).ToArray();
        if (conflicts.Length > 0)
            throw new InvalidDataException($"Un SKU no puede estar simultáneamente en stock[] y eliminados[]: {string.Join(", ", conflicts)}.");

        var orderedStock = stock.OrderBy(x => x.CInterno).ToList();
        var orderedDeleted = deleted.Values.OrderBy(x => x.CInterno).ToList();
        var skuValidos = catalog.Productos.Count;
        var skuStockCero = skuValidos - orderedStock.Count - orderedDeleted.Count;
        if (skuStockCero < 0)
            throw new InvalidDataException("El resumen generado viola la invariante de SKU válidos.");

        progress?.Report("Preparando snapshot...");
        var now = DateTimeOffset.UtcNow.ToOffset(PeruOffset);
        var snapshot = new SnapshotDocument
        {
            ContractVersion = 2,
            SnapshotId = Guid.NewGuid(),
            CapturadoAt = now.ToString("O", CultureInfo.InvariantCulture),
            VersionCatalogo = catalog.CatalogVersion,
            ExcelHash = HashService.Sha256File(excelPath),
            StoreName = storeDisplay,
            SourceFileName = Path.GetFileName(excelPath),
            Stock = orderedStock,
            Eliminados = orderedDeleted,
            Incidencias = incidents,
            Resumen = new SnapshotSummary
            {
                SkuTotalExcel = skuTotalExcel,
                SkuCatalogo = catalog.Productos.Count + catalog.Excluidos.Count,
                SkuExcluidos = catalog.Excluidos.Count,
                SkuValidos = skuValidos,
                SkuStockNoCero = orderedStock.Count,
                SkuStockCero = skuStockCero,
                SkuEliminados = orderedDeleted.Count,
                IncidenciasTotal = incidents.Count
            }
        };

        return new ProcessingResult { Snapshot = snapshot };
    }

    private static void Compare(CatalogProduct cp, string name, string barcode, decimal? price, int row, List<Incident> list)
    {
        if (!string.Equals(NormalizeText(cp.Producto), NormalizeText(name), StringComparison.OrdinalIgnoreCase))
            list.Add(NewIncident(cp.CInterno, "nombre_modificado", ("fila", row), ("anterior", cp.Producto), ("nuevo", name)));

        var oldB = NormalizeBarcode(cp.CBarras);
        var newB = NormalizeBarcode(barcode);
        // El backend V2 considera equivalentes códigos de barras que solo difieren
        // por espacios en blanco. Conservamos la forma legible en las incidencias,
        // pero comparamos una representación canónica sin whitespace.
        if (!string.Equals(NormalizeBarcodeForComparison(oldB), NormalizeBarcodeForComparison(newB), StringComparison.Ordinal))
        {
            var type = string.IsNullOrEmpty(oldB)
                ? "codigo_barras_agregado"
                : string.IsNullOrEmpty(newB)
                    ? "codigo_barras_eliminado"
                    : "codigo_barras_modificado";
            list.Add(NewIncident(cp.CInterno, type, ("fila", row), ("anterior", NullIfBlank(oldB)), ("nuevo", NullIfBlank(newB))));
        }

        // decimal compara valor numérico, por lo que 1.5m == 1.50m == 1.500m.
        if (price.HasValue && price.Value != cp.Precio)
            list.Add(NewIncident(cp.CInterno, "precio_modificado", ("fila", row), ("anterior", cp.Precio), ("nuevo", price.Value)));
    }

    private static Incident NewIncident(int code, string type, params (string Key, object? Value)[] data)
        => new() { CInterno = code, Tipo = type, Datos = CompactData(data) };

    private static Dictionary<string, object?> CompactData(params (string Key, object? Value)[] data)
    {
        var result = new Dictionary<string, object?>();
        foreach (var (key, value) in data)
            result[key] = value;
        return result;
    }

    private static void ValidateFile(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("No se encontró el archivo seleccionado.", path);
        if (!string.Equals(Path.GetExtension(path), ".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Solo se admiten archivos .xlsx.");
        if (new FileInfo(path).Length == 0) throw new InvalidDataException("El archivo está vacío.");
    }


    private static bool IsAuxiliaryCode(int? code)
        => code.HasValue && code.Value is >= 1000 and <= 4000;

    private static int? ParseCode(string raw)
    {
        var text = raw.Trim();
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return v;

        if ((decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
             || decimal.TryParse(text, NumberStyles.Float, new CultureInfo("es-PE"), out d))
            && d == decimal.Truncate(d) && d >= int.MinValue && d <= int.MaxValue)
            return (int)d;
        return null;
    }

    private static bool TryParseWholeNumber(string raw, out int value)
    {
        value = 0;
        if (!TryParseDecimal(raw, out var d) || d != decimal.Truncate(d) || d < int.MinValue || d > int.MaxValue) return false;
        value = (int)d;
        return true;
    }

    private static bool TryParseDecimal(string raw, out decimal value)
    {
        var text = raw.Trim();
        return decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            || decimal.TryParse(text, NumberStyles.Float, new CultureInfo("es-PE"), out value);
    }

    // Los números almacenados por Excel siguen semántica IEEE-754 y pueden aparecer
    // en el XML con residuos como 2.2000000000000002 aunque Excel muestre 2.2.
    // Excel trabaja con un máximo de 15 dígitos significativos; normalizamos la
    // lectura de Precio venta a esa precisión antes de comparar o serializar.
    private static bool TryParsePrice(string raw, out decimal value)
    {
        value = 0m;
        var text = raw.Trim();
        if (text.Length == 0) return false;

        if (!(double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
              || double.TryParse(text, NumberStyles.Float, new CultureInfo("es-PE"), out number))
            || double.IsNaN(number)
            || double.IsInfinity(number))
            return false;

        var normalized = number.ToString("G15", CultureInfo.InvariantCulture);
        return decimal.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static string? ResolveStoreFromWarehouse(string? warehouse)
    {
        var value = NormalizeWarehouse(warehouse);
        if (value.Length == 0) return null;

        var matches = new List<string>(2);
        if (value.Contains("HUACACHINA", StringComparison.Ordinal) || value.Contains("HUACA", StringComparison.Ordinal)) matches.Add("Huaca");
        if (value.Contains("CUTERVO", StringComparison.Ordinal)) matches.Add("Cutervo");
        if (value.Contains("DIVINO", StringComparison.Ordinal)) matches.Add("Divino");
        if (value.Contains("UNIDAD", StringComparison.Ordinal)) matches.Add("Unidad");
        if (value.Contains("CASUARINAS", StringComparison.Ordinal) || value.Contains("CASUARINA", StringComparison.Ordinal)) matches.Add("Casuarinas");

        return matches.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1 ? matches[0] : null;
    }

    private static string NormalizeStoreName(string? value)
    {
        var normalized = NormalizeWarehouse(value);
        if (normalized.Contains("HUACACHINA", StringComparison.Ordinal) || normalized.Contains("HUACA", StringComparison.Ordinal)) return "HUACA";
        if (normalized.Contains("CUTERVO", StringComparison.Ordinal)) return "CUTERVO";
        if (normalized.Contains("DIVINO", StringComparison.Ordinal)) return "DIVINO";
        if (normalized.Contains("UNIDAD", StringComparison.Ordinal)) return "UNIDAD";
        if (normalized.Contains("CASUARINAS", StringComparison.Ordinal) || normalized.Contains("CASUARINA", StringComparison.Ordinal)) return "CASUARINAS";
        return normalized;
    }

    private static string NormalizeWarehouse(string? value)
    {
        var source = (value ?? "").Trim().ToUpperInvariant().Normalize(NormalizationForm.FormD);
        var chars = source
            .Where(ch => CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            .Where(char.IsLetterOrDigit)
            .ToArray();
        return new string(chars).Normalize(NormalizationForm.FormC);
    }

    private static string NormalizeText(string? value) => Regex.Replace((value ?? "").Trim(), @"\s+", " ");
    private static string NormalizeBarcode(string? value)
    {
        var text = (value ?? "").Trim();
        if (text.EndsWith(".0", StringComparison.Ordinal)) text = text[..^2];
        return text;
    }

    private static string NormalizeBarcodeForComparison(string? value)
        => Regex.Replace(NormalizeBarcode(value), @"\s+", "");
    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record ParsedRow(ExcelProductRow Row, int RowNumber, int? Code);
}
