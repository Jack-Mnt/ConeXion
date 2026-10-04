using System.IO;
using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using System.Xml;

namespace ConeXion.Core.Services;

using ConeXion.Core.Models;

public static class XlsxReader
{
    private static readonly string[] RequiredHeaders =
    [
        "Nombre de Tienda",
        "Nombre de Almacén",
        "Nombre",
        "C. interno",
        "C. barras",
        "Precio venta",
        "Stock"
    ];

    public static List<ExcelProductRow> ReadProducts(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var sharedStrings = ReadSharedStrings(archive);
        var worksheetPath = ResolveFirstWorksheetPath(archive);
        var worksheetEntry = archive.GetEntry(worksheetPath)
            ?? throw new InvalidDataException("No se encontró la primera hoja del Excel.");

        using var sheetStream = worksheetEntry.Open();
        using var reader = XmlReader.Create(sheetStream, new XmlReaderSettings
        {
            IgnoreComments = true,
            IgnoreWhitespace = true
        });

        Dictionary<int, string>? headerByColumn = null;
        Dictionary<string, int>? columnByHeader = null;
        var rows = new List<ExcelProductRow>(1024);

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "row")
                continue;

            var rowNumberText = reader.GetAttribute("r");
            using var rowSubtree = reader.ReadSubtree();
            var cells = ReadRowCells(rowSubtree, sharedStrings);
            if (cells.Count == 0)
                continue;


            var rowNumber = int.TryParse(rowNumberText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rn) ? rn : rows.Count + 2;

            if (headerByColumn is null)
            {
                headerByColumn = cells
                    .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Value))
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Trim());

                columnByHeader = headerByColumn
                    .GroupBy(kvp => kvp.Value, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First().Key, StringComparer.OrdinalIgnoreCase);

                var missing = RequiredHeaders.Where(h => !columnByHeader.ContainsKey(h)).ToArray();
                if (missing.Length > 0)
                    throw new InvalidDataException($"Faltan columnas obligatorias: {string.Join(", ", missing)}.");

                continue;
            }

            if (columnByHeader is null)
                throw new InvalidOperationException("No se pudo interpretar la cabecera del Excel.");

            string Get(string header)
            {
                var col = columnByHeader[header];
                return cells.TryGetValue(col, out var value) ? value.Trim() : "";
            }

            var internalCode = Get("C. interno");
            var productName = Get("Nombre");
            var stock = Get("Stock");

            // Ignore completely blank rows.
            if (string.IsNullOrWhiteSpace(internalCode) && string.IsNullOrWhiteSpace(productName) && string.IsNullOrWhiteSpace(stock))
                continue;

            rows.Add(new ExcelProductRow
            {
                RowNumber = rowNumber,
                StoreName = Get("Nombre de Tienda"),
                WarehouseName = Get("Nombre de Almacén"),
                ProductName = productName,
                InternalCodeRaw = internalCode,
                Barcode = Get("C. barras"),
                PriceRaw = Get("Precio venta"),
                StockRaw = stock
            });
        }

        if (headerByColumn is null)
            throw new InvalidDataException("El Excel no contiene una cabecera válida.");

        if (rows.Count == 0)
            throw new InvalidDataException("El Excel no contiene productos.");

        return rows;
    }

    private static List<string> ReadSharedStrings(ZipArchive archive)
    {
        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry is null)
            return [];

        using var stream = entry.Open();
        var doc = XDocument.Load(stream);
        var ns = doc.Root?.Name.Namespace ?? XNamespace.None;

        return doc.Descendants(ns + "si")
            .Select(si => string.Concat(si.Descendants(ns + "t").Select(t => t.Value)))
            .ToList();
    }

    private static string ResolveFirstWorksheetPath(ZipArchive archive)
    {
        var workbookEntry = archive.GetEntry("xl/workbook.xml")
            ?? throw new InvalidDataException("El archivo no contiene xl/workbook.xml.");
        var relsEntry = archive.GetEntry("xl/_rels/workbook.xml.rels")
            ?? throw new InvalidDataException("El archivo no contiene relaciones del workbook.");

        string relationshipId;
        using (var stream = workbookEntry.Open())
        {
            var doc = XDocument.Load(stream);
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            XNamespace r = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
            var sheet = doc.Descendants(ns + "sheet").FirstOrDefault()
                ?? throw new InvalidDataException("El workbook no contiene hojas.");
            relationshipId = sheet.Attribute(r + "id")?.Value
                ?? throw new InvalidDataException("La primera hoja no tiene relación válida.");
        }

        using (var stream = relsEntry.Open())
        {
            var doc = XDocument.Load(stream);
            XNamespace rel = "http://schemas.openxmlformats.org/package/2006/relationships";
            var relationship = doc.Descendants(rel + "Relationship")
                .FirstOrDefault(x => string.Equals((string?)x.Attribute("Id"), relationshipId, StringComparison.Ordinal));
            var target = relationship?.Attribute("Target")?.Value
                ?? throw new InvalidDataException("No se pudo resolver la primera hoja.");

            target = target.Replace('\\', '/');
            if (target.StartsWith('/'))
                return target.TrimStart('/');
            if (target.StartsWith("xl/", StringComparison.OrdinalIgnoreCase))
                return target;
            return "xl/" + target.TrimStart('/');
        }
    }

    private static Dictionary<int, string> ReadRowCells(XmlReader rowReader, IReadOnlyList<string> sharedStrings)
    {
        var result = new Dictionary<int, string>();
        while (rowReader.Read())
        {
            if (rowReader.NodeType != XmlNodeType.Element || rowReader.LocalName != "c")
                continue;

            var cellRef = rowReader.GetAttribute("r") ?? "";
            var type = rowReader.GetAttribute("t") ?? "";
            var column = ColumnIndexFromCellReference(cellRef);
            if (column < 0)
                continue;

            string value = "";
            using var cellSubtree = rowReader.ReadSubtree();
            while (cellSubtree.Read())
            {
                if (cellSubtree.NodeType != XmlNodeType.Element)
                    continue;

                if (cellSubtree.LocalName == "v")
                {
                    value = cellSubtree.ReadElementContentAsString();
                }
                else if (cellSubtree.LocalName == "t" && type == "inlineStr")
                {
                    value += cellSubtree.ReadElementContentAsString();
                }
            }

            if (type == "s" && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            {
                value = index >= 0 && index < sharedStrings.Count ? sharedStrings[index] : "";
            }

            result[column] = value;
        }

        return result;
    }


    private static int ColumnIndexFromCellReference(string cellReference)
    {
        var index = 0;
        var found = false;
        foreach (var ch in cellReference)
        {
            if (!char.IsLetter(ch))
                break;
            found = true;
            index = index * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
        }
        return found ? index - 1 : -1;
    }
}
