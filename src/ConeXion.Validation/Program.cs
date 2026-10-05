using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using ConeXion.Core.Models;
using ConeXion.Core.Services;

var tests = new (string Name, Action Test)[]
{
    ("CatalogCodec acepta schema V2 productivo", CatalogCodecAcceptsV2),
    ("CatalogCodec rechaza schema legacy", CatalogCodecRejectsLegacy),
    ("InventoryProcessor genera snapshot V2", InventoryProcessorBuildsV2),
    ("InventoryProcessor preserva stock negativo", InventoryProcessorKeepsNegative),
    ("InventoryProcessor maneja duplicado conocido", InventoryProcessorKnownDuplicate),
    ("InventoryProcessor maneja stock inválido conocido", InventoryProcessorInvalidStock),
    ("InventoryProcessor conserva código inválido sin original", InventoryProcessorInvalidCodeWithoutOriginal),
    ("InventoryProcessor detecta cambios comerciales V2", InventoryProcessorCommercialChanges),
    ("InventoryProcessor ignora diferencias de whitespace en código de barras", InventoryProcessorIgnoresBarcodeWhitespace),
    ("InventoryProcessor normaliza residuos binarios de precio Excel", InventoryProcessorNormalizesExcelPriceArtifact),
    ("InventoryProcessor producto_nuevo nunca entra en stock", InventoryProcessorNewProductsOnlyIncident),
    ("InventoryProcessor rechaza precio negativo de producto nuevo", InventoryProcessorRejectsNegativeNewPrice),
    ("SnapshotUsability permite snapshot resoluble", SnapshotUsabilityAllowsResolvedStock),
    ("SnapshotUsability permite producto ausente como cero", SnapshotUsabilityAllowsMissingProduct),
    ("SnapshotUsability bloquea código interno duplicado", SnapshotUsabilityBlocksDuplicate),
    ("SnapshotUsability bloquea stock inválido", SnapshotUsabilityBlocksInvalidStock),
    ("SnapshotUsability bloquea por defecto motivos no resolubles", SnapshotUsabilityBlocksUnknownDeletedReason),
    ("Serialización V2 no contiene ignorados", SerializationIsExactV2),
    ("SupabaseGateway consume estado plano y codigo", GatewayUsesV2Contracts),
};

var failed = 0;
foreach (var (name, test) in tests)
{
    try
    {
        test();
        Console.WriteLine($"OK  {name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {name}: {ex.Message}");
    }
}

if (failed > 0)
{
    Console.Error.WriteLine($"{failed} validación(es) fallaron.");
    return 1;
}
Console.WriteLine($"{tests.Length} validaciones aprobadas.");
return 0;

static void CatalogCodecAcceptsV2()
{
    const string json = """
    {"schema_version":2,"catalog_version":6,"generated_at":"2026-09-09T20:00:00Z","productos":[{"c_interno":20101,"producto":"A","c_barras":null,"precio":1.50}],"excluidos":[{"c_interno":21286,"producto":"X"}]}
    """;
    var package = Package(json);
    var catalog = CatalogCodec.Decode(package);
    Eq(2, catalog.SchemaVersion, "schema");
    Eq(6, catalog.CatalogVersion, "catalog_version");
    Eq(1, catalog.Productos.Count, "productos");
    Eq(1, catalog.Excluidos.Count, "excluidos");
    Eq(1.50m, catalog.Productos[0].Precio, "precio");
}

static void CatalogCodecRejectsLegacy()
{
    const string legacy = """
    {"schema_version":1,"version":5,"fecha":"2026-09-01","productos":[],"excluidos":[]}
    """;
    Throws<InvalidDataException>(() => CatalogCodec.Decode(Package(legacy)));
}

static void InventoryProcessorBuildsV2()
{
    var catalog = new CatalogDocument
    {
        SchemaVersion = 2,
        CatalogVersion = 6,
        GeneratedAt = "2026-09-09T20:00:00Z",
        Productos =
        [
            new() { CInterno = 20101, Producto = "PRODUCTO A", CBarras = "111", Precio = 1.5m },
            new() { CInterno = 20102, Producto = "PRODUCTO B", CBarras = null, Precio = 2m },
            new() { CInterno = 20103, Producto = "PRODUCTO C", CBarras = "333", Precio = 3m },
        ],
        Excluidos = [new() { CInterno = 21286, Producto = "EXCLUIDO" }]
    };

    var file = CreateXlsx(
        Row("HUACA", "PRODUCTO A", "20101", "111", "1.50", "5"),
        Row("HUACA", "PRODUCTO B", "20102", "999", "2.00", "0"),
        Row("HUACA", "EXCLUIDO MODIFICADO", "21286", "ZZZ", "99", "7"),
        Row("HUACA", "NUEVO", "21500", "", "5.5", "-2"),
        Row("HUACA", "CODIGO MALO", "20A43", "", "1", "4"),
        Row("HUACA", "AUXILIAR", "2050", "", "0", "100")
    );

    try
    {
        var s = new InventoryProcessor().Process(file, catalog, "Huaca").Snapshot;
        Eq(2, s.ContractVersion, "contract_version");
        Eq(6, s.VersionCatalogo, "version_catalogo");
        Eq(1, s.Stock.Count, "stock count");
        Eq(20101, s.Stock[0].CInterno, "stock sku");
        Eq(5, s.Stock[0].Stock, "stock value");
        True(s.Stock.All(x => x.CInterno != 21500), "producto_nuevo no debe entrar en stock[]");
        True(s.Stock.All(x => x.CInterno != 21286), "excluido no debe entrar en stock[]");

        var absent = Single(s.Eliminados, x => x.CInterno == 20103);
        Eq("producto_ausente", absent.Motivo, "motivo ausente");
        True(s.Eliminados.All(x => x.CInterno != 21286), "excluido no debe entrar en eliminados[]");

        var nuevo = Single(s.Incidencias, x => x.Tipo == "producto_nuevo");
        Eq(21500, nuevo.CInterno, "producto nuevo code");
        Eq(-2, Convert.ToInt32(nuevo.Datos["stock"]), "producto nuevo stock negativo");
        True(nuevo.Datos.ContainsKey("c_barras") && nuevo.Datos["c_barras"] is null, "producto_nuevo debe conservar c_barras null");
        True(!s.Incidencias.Any(x => x.Tipo == "precio_modificado" && x.CInterno == 20101), "1.5 vs 1.50 no debe generar precio_modificado");
        var barcodeAdded = Single(s.Incidencias, x => x.Tipo == "codigo_barras_agregado" && x.CInterno == 20102);
        True(barcodeAdded.Datos.ContainsKey("anterior") && barcodeAdded.Datos["anterior"] is null, "barcode agregado debe conservar anterior=null");
        Eq("999", Convert.ToString(barcodeAdded.Datos["nuevo"]), "barcode agregado nuevo");
        var invalid = Single(s.Incidencias, x => x.Tipo == "codigo_interno_invalido");
        True(invalid.CInterno is null, "codigo_interno_invalido debe usar null");
        Eq("20A43", invalid.CInternoOriginal, "codigo original");
        True(!s.Incidencias.Any(x => x.CInterno == 21286), "excluido no debe generar incidencias comerciales");

        Eq(5, s.Resumen.SkuTotalExcel, "sku_total_excel");
        Eq(4, s.Resumen.SkuCatalogo, "sku_catalogo");
        Eq(1, s.Resumen.SkuExcluidos, "sku_excluidos");
        Eq(3, s.Resumen.SkuValidos, "sku_validos");
        Eq(1, s.Resumen.SkuStockNoCero, "sku_stock_no_cero");
        Eq(1, s.Resumen.SkuStockCero, "sku_stock_cero");
        Eq(1, s.Resumen.SkuEliminados, "sku_eliminados");
        Eq(s.Incidencias.Count, s.Resumen.IncidenciasTotal, "incidencias_total");
        Eq(s.Resumen.SkuValidos, s.Resumen.SkuStockNoCero + s.Resumen.SkuStockCero + s.Resumen.SkuEliminados, "invariante");
    }
    finally { File.Delete(file); }
}

static void InventoryProcessorKeepsNegative()
{
    var catalog = CatalogOne(20101, "A", "1", 1m);
    var file = CreateXlsx(Row("HUACA", "A", "20101", "1", "1.00", "-4"));
    try
    {
        var s = new InventoryProcessor().Process(file, catalog, "Huaca").Snapshot;
        Eq(-4, Single(s.Stock, x => x.CInterno == 20101).Stock, "stock negativo");
        Eq(0, s.Eliminados.Count, "eliminados");
    }
    finally { File.Delete(file); }
}

static void InventoryProcessorKnownDuplicate()
{
    var catalog = CatalogOne(20101, "A", "1", 1m);
    var file = CreateXlsx(
        Row("HUACA", "A", "20101", "1", "1", "5"),
        Row("HUACA", "A", "20101", "1", "1", "7"));
    try
    {
        var s = new InventoryProcessor().Process(file, catalog, "Huaca").Snapshot;
        Eq(0, s.Stock.Count, "duplicate stock");
        Eq("codigo_interno_duplicado", Single(s.Eliminados, x => x.CInterno == 20101).Motivo, "duplicate motive");
        True(s.Incidencias.Any(x => x.Tipo == "codigo_interno_duplicado" && x.CInterno == 20101), "duplicate incident");
        True(!s.Incidencias.Any(x => x.Tipo == "producto_ausente" && x.CInterno == 20101), "duplicate must not also be absent");
    }
    finally { File.Delete(file); }
}

static void InventoryProcessorInvalidStock()
{
    var catalog = CatalogOne(20101, "A", "1", 1m);
    var file = CreateXlsx(Row("HUACA", "A", "20101", "1", "1", "NO_NUMERICO"));
    try
    {
        var s = new InventoryProcessor().Process(file, catalog, "Huaca").Snapshot;
        Eq(0, s.Stock.Count, "invalid stock[]");
        Eq("stock_invalido", Single(s.Eliminados, x => x.CInterno == 20101).Motivo, "invalid stock motive");
        var incident = Single(s.Incidencias, x => x.Tipo == "stock_invalido");
        Eq("NO_NUMERICO", Convert.ToString(incident.Datos["valor_original"]), "stock original");
    }
    finally { File.Delete(file); }
}

static void InventoryProcessorInvalidCodeWithoutOriginal()
{
    var catalog = CatalogOne(20101, "A", "1", 1m);
    var file = CreateXlsx(
        Row("HUACA", "A", "20101", "1", "1", "0"),
        Row("HUACA", "SIN CODIGO", "", "", "2", "3"));
    try
    {
        var s = new InventoryProcessor().Process(file, catalog, "Huaca").Snapshot;
        var incident = Single(s.Incidencias, x => x.Tipo == "codigo_interno_invalido");
        True(incident.CInterno is null, "c_interno debe ser null");
        True(incident.CInternoOriginal is null, "c_interno_original puede ser null cuando la celda está vacía");
        Eq(2, s.Resumen.SkuTotalExcel, "sku_total_excel con fila de producto identificable sin código");
    }
    finally { File.Delete(file); }
}

static void InventoryProcessorCommercialChanges()
{
    var catalog = new CatalogDocument
    {
        SchemaVersion = 2,
        CatalogVersion = 6,
        GeneratedAt = "2026-09-09T20:00:00Z",
        Productos =
        [
            new() { CInterno = 20101, Producto = "A", CBarras = "111", Precio = 1.5m },
            new() { CInterno = 20102, Producto = "B", CBarras = "222", Precio = 2m },
            new() { CInterno = 20103, Producto = "C", CBarras = null, Precio = 3m },
            new() { CInterno = 20104, Producto = "D", CBarras = "444", Precio = 4m },
        ]
    };
    var file = CreateXlsx(
        Row("HUACA", "A CAMBIADO", "20101", "111", "1.80", "0"),
        Row("HUACA", "B", "20102", "", "2", "0"),
        Row("HUACA", "C", "20103", "333", "3", "0"),
        Row("HUACA", "D", "20104", "555", "4", "0"));
    try
    {
        var s = new InventoryProcessor().Process(file, catalog, "Huaca").Snapshot;
        True(s.Incidencias.Any(x => x.CInterno == 20101 && x.Tipo == "nombre_modificado"), "nombre_modificado");
        True(s.Incidencias.Any(x => x.CInterno == 20101 && x.Tipo == "precio_modificado"), "precio_modificado");
        True(s.Incidencias.Any(x => x.CInterno == 20102 && x.Tipo == "codigo_barras_eliminado"), "barcode eliminado");
        True(s.Incidencias.Any(x => x.CInterno == 20103 && x.Tipo == "codigo_barras_agregado"), "barcode agregado");
        True(s.Incidencias.Any(x => x.CInterno == 20104 && x.Tipo == "codigo_barras_modificado"), "barcode modificado");
        Eq(0, s.Stock.Count, "stocks cero omitidos");
        Eq(4, s.Resumen.SkuStockCero, "sku_stock_cero");
    }
    finally { File.Delete(file); }
}

static void InventoryProcessorIgnoresBarcodeWhitespace()
{
    var catalog = CatalogOne(21216, "A", "691584 024669", 40m);
    var file = CreateXlsx(Row("HUACA", "A", "21216", "691584024669", "40", "0"));
    try
    {
        var s = new InventoryProcessor().Process(file, catalog, "Huaca").Snapshot;
        True(!s.Incidencias.Any(x => x.CInterno == 21216 && x.Tipo.StartsWith("codigo_barras_", StringComparison.Ordinal)),
            "un código de barras que solo cambia por whitespace no debe generar incidencia");
        Eq(0, s.Incidencias.Count, "incidencias whitespace barcode");
        Eq(1, s.Resumen.SkuStockCero, "sku_stock_cero whitespace barcode");
    }
    finally { File.Delete(file); }
}


static void InventoryProcessorNormalizesExcelPriceArtifact()
{
    var catalog = CatalogOne(20266, "A", "1", 2.2m);
    var file = CreateXlsx(Row("HUACA", "A", "20266", "1", "2.2000000000000002", "3"));
    try
    {
        var s = new InventoryProcessor().Process(file, catalog, "Huaca").Snapshot;
        True(!s.Incidencias.Any(x => x.CInterno == 20266 && x.Tipo == "precio_modificado"),
            "2.2000000000000002 debe normalizarse al valor Excel 2.2");
        Eq(3, Single(s.Stock, x => x.CInterno == 20266).Stock, "stock debe preservarse");
    }
    finally { File.Delete(file); }
}

static void InventoryProcessorNewProductsOnlyIncident()
{
    var catalog = CatalogOne(20101, "A", "1", 1m);
    var file = CreateXlsx(
        Row("HUACA", "A", "20101", "1", "1", "0"),
        Row("HUACA", "NUEVO POSITIVO", "21501", "", "5", "7"),
        Row("HUACA", "NUEVO CERO", "21502", "", "5", "0"),
        Row("HUACA", "NUEVO NEGATIVO", "21503", "", "5", "-7"));
    try
    {
        var s = new InventoryProcessor().Process(file, catalog, "Huaca").Snapshot;
        Eq(3, s.Incidencias.Count(x => x.Tipo == "producto_nuevo"), "producto_nuevo count");
        True(s.Stock.All(x => x.CInterno is not (21501 or 21502 or 21503)), "productos nuevos fuera de stock[]");
        True(s.Eliminados.All(x => x.CInterno is not (21501 or 21502 or 21503)), "productos nuevos fuera de eliminados[]");
        var stocks = s.Incidencias.Where(x => x.Tipo == "producto_nuevo").Select(x => Convert.ToInt32(x.Datos["stock"])).OrderBy(x => x).ToArray();
        True(stocks.SequenceEqual(new[] { -7, 0, 7 }), "stocks producto_nuevo");
    }
    finally { File.Delete(file); }
}

static void InventoryProcessorRejectsNegativeNewPrice()
{
    var catalog = CatalogOne(20101, "A", "1", 1m);
    var file = CreateXlsx(
        Row("HUACA", "A", "20101", "1", "1", "0"),
        Row("HUACA", "NUEVO", "21500", "", "-1", "3"));
    try { Throws<InvalidDataException>(() => new InventoryProcessor().Process(file, catalog, "Huaca")); }
    finally { File.Delete(file); }
}


static void SnapshotUsabilityAllowsResolvedStock()
{
    var snapshot = new SnapshotDocument
    {
        Stock = [new StockItem { CInterno = 20101, Stock = -4 }],
        Incidencias =
        [
            new Incident
            {
                CInterno = 20101,
                Tipo = "precio_modificado",
                Datos = new() { ["anterior"] = 1m, ["nuevo"] = 1.5m }
            }
        ]
    };

    var result = SnapshotUsabilityValidator.Validate(snapshot, CatalogOne(20101, "A", "1", 1m));
    True(result.IsUsable, "stock conocido y cambios administrativos deben ser utilizables");
    Eq(0, result.Blockers.Count, "blockers snapshot resoluble");
}

static void SnapshotUsabilityAllowsMissingProduct()
{
    var snapshot = new SnapshotDocument
    {
        Eliminados = [new DeletedProduct { CInterno = 20101, Motivo = "producto_ausente" }],
        Incidencias =
        [
            new Incident { CInterno = 20101, Tipo = "producto_ausente", Datos = new() { ["producto"] = "A" } }
        ]
    };

    var result = SnapshotUsabilityValidator.Validate(snapshot, CatalogOne(20101, "A", "1", 1m));
    True(result.IsUsable, "producto_ausente debe resolverse como stock 0");
    True(SnapshotUsabilityValidator.TryResolveDeletedStock(snapshot.Eliminados[0], out var stock), "producto_ausente resoluble");
    Eq(0, stock, "producto_ausente stock lógico");
}

static void SnapshotUsabilityBlocksDuplicate()
{
    var snapshot = new SnapshotDocument
    {
        Eliminados = [new DeletedProduct { CInterno = 20362, Motivo = "codigo_interno_duplicado" }],
        Incidencias =
        [
            new Incident
            {
                CInterno = 20362,
                Tipo = "codigo_interno_duplicado",
                Datos = new()
                {
                    ["filas"] = new[] { 265, 906 },
                    ["valores_stock"] = new string?[] { "0", "7" },
                    ["producto"] = "CASINO CAFE VIBES"
                }
            }
        ]
    };

    // SyncCoordinator valida payloads deserializados de SQLite; este roundtrip
    // cubre también la lectura de datos como JsonElement.
    var persisted = SnapshotExporter.Deserialize(SnapshotExporter.SerializeCompact(snapshot));
    var catalog = CatalogOne(20362, "CASINO CAFE VIBES", "1", 1m);
    var result = SnapshotUsabilityValidator.Validate(persisted, catalog);

    True(!result.IsUsable, "duplicado debe bloquear");
    var blocker = Single(result.Blockers, x => x.CInterno == 20362);
    Eq("codigo_interno_duplicado", blocker.Motivo, "motivo duplicado");
    Eq("CASINO CAFE VIBES", blocker.Producto, "producto duplicado");
    Eq(2, blocker.Observations.Count, "observaciones duplicado");
    Eq<int?>(265, blocker.Observations[0].Row, "fila duplicado 1");
    Eq("0", blocker.Observations[0].Value, "stock duplicado 1");
    Eq<int?>(906, blocker.Observations[1].Row, "fila duplicado 2");
    Eq("7", blocker.Observations[1].Value, "stock duplicado 2");
}

static void SnapshotUsabilityBlocksInvalidStock()
{
    var snapshot = new SnapshotDocument
    {
        Eliminados = [new DeletedProduct { CInterno = 20101, Motivo = "stock_invalido" }],
        Incidencias =
        [
            new Incident
            {
                CInterno = 20101,
                Tipo = "stock_invalido",
                Datos = new()
                {
                    ["fila"] = 55,
                    ["producto"] = "A",
                    ["valor_original"] = "7 unidades"
                }
            }
        ]
    };

    var result = SnapshotUsabilityValidator.Validate(snapshot, CatalogOne(20101, "A", "1", 1m));
    True(!result.IsUsable, "stock inválido debe bloquear");
    var blocker = Single(result.Blockers, x => x.CInterno == 20101);
    Eq<int?>(55, blocker.Observations[0].Row, "fila stock inválido");
    Eq("7 unidades", blocker.Observations[0].Value, "valor stock inválido");
}

static void SnapshotUsabilityBlocksUnknownDeletedReason()
{
    var snapshot = new SnapshotDocument
    {
        Eliminados = [new DeletedProduct { CInterno = 20101, Motivo = "motivo_futuro_sin_resolucion" }]
    };

    var result = SnapshotUsabilityValidator.Validate(snapshot, CatalogOne(20101, "A", "1", 1m));
    True(!result.IsUsable, "un motivo futuro sin semántica de stock debe bloquear por defecto");
}

static void SerializationIsExactV2()
{
    var snapshot = new SnapshotDocument
    {
        SnapshotId = Guid.Parse("10000000-0000-4000-8000-000000000001"),
        CapturadoAt = "2026-09-09T16:00:00-05:00",
        VersionCatalogo = 6,
        ExcelHash = new string('a', 64),
        Incidencias =
        [
            new Incident { CInterno = null, CInternoOriginal = "20A43", Tipo = "codigo_interno_invalido", Datos = new() { ["fila"] = 58 } }
        ]
    };
    var json = SnapshotExporter.SerializeCompact(snapshot);
    using var doc = JsonDocument.Parse(json);
    var root = doc.RootElement;
    Eq(2, root.GetProperty("contract_version").GetInt32(), "contract_version json");
    True(!root.TryGetProperty("ignorados", out _), "ignorados no debe serializarse");
    var incident = root.GetProperty("incidencias")[0];
    True(incident.TryGetProperty("c_interno", out var ci) && ci.ValueKind == JsonValueKind.Null, "c_interno null debe conservarse");
    Eq("20A43", incident.GetProperty("c_interno_original").GetString(), "c_interno_original");
}

static void GatewayUsesV2Contracts()
{
    var handler = new QueueHandler(
        _ => Json(HttpStatusCode.OK, """{"ok":true,"codigo":"OK","sede_id":"s1","sede_nombre":"Huaca","installation_estado":"activa","installation_catalog_version":6,"catalog_version":6,"catalog_schema_version":2,"catalog_hash":"abc","catalog_storage_path":"conexion-catalogos/v6/catalog_v6.prcatalog","catalog_size":25004,"catalog_update_required":false,"ultimo_snapshot_confirmado_at":null}"""),
        req =>
        {
            var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            True(body.Contains("\"confirm_catalog_version\":6", StringComparison.Ordinal), "confirm_catalog_version request");
            True(body.Contains("\"confirm_catalog_hash\":\"abc\"", StringComparison.Ordinal), "confirm_catalog_hash request");
            return Json(HttpStatusCode.OK, """{"ok":true,"codigo":"OK","catalog_update_required":false}""");
        },
        _ => Json(HttpStatusCode.Created, """{"ok":true,"codigo":"OK","snapshot_id":"10000000-0000-4000-8000-000000000001","duplicado":false,"ultimo_snapshot_confirmado_at":"2026-09-09T21:00:00Z","stock_rows":3,"incidencias_procesadas":1}""")
    );
    using var gateway = new SupabaseGateway("https://unit.test", handler);
    var creds = new InstallationCredentials("iid", "secret");
    var state = gateway.GetStateAsync(creds, "2.1.0", 6, "abc").GetAwaiter().GetResult();
    True(state.Ok, "state ok");
    Eq("OK", state.Codigo, "state codigo");
    Eq(2, state.CatalogSchemaVersion, "state schema");
    True(gateway.ConfirmCatalogAsync(creds, "2.1.0", 6, "abc").GetAwaiter().GetResult(), "confirm catalog");
    var upload = gateway.UploadSnapshotAsync(creds, "2.1.0", new SnapshotDocument { SnapshotId = Guid.Parse("10000000-0000-4000-8000-000000000001") }).GetAwaiter().GetResult();
    True(upload.Accepted, "upload accepted");
    Eq("OK", upload.Codigo, "upload codigo");
    Eq(3, upload.StockRows, "stock rows");
}

static CatalogDocument CatalogOne(int code, string name, string barcode, decimal price) => new()
{
    SchemaVersion = 2,
    CatalogVersion = 6,
    GeneratedAt = "2026-09-09T20:00:00Z",
    Productos = [new CatalogProduct { CInterno = code, Producto = name, CBarras = barcode, Precio = price }]
};

static byte[] Package(string json)
{
    using var output = new MemoryStream();
    using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        gzip.Write(bytes, 0, bytes.Length);
    }
    return Encoding.UTF8.GetBytes(Convert.ToBase64String(output.ToArray()));
}

static string[] Row(string warehouse, string product, string code, string barcode, string price, string stock)
    => ["TIENDA", warehouse, product, code, barcode, price, stock];

static string CreateXlsx(params string[][] productRows)
{
    var path = Path.Combine(Path.GetTempPath(), $"conexion-v2-{Guid.NewGuid():N}.xlsx");
    using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
    Write(zip, "xl/workbook.xml", """<?xml version="1.0" encoding="UTF-8"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Stock" sheetId="1" r:id="rId1"/></sheets></workbook>""");
    Write(zip, "xl/_rels/workbook.xml.rels", """<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>""");

    var all = new List<string[]> { new[] { "Nombre de Tienda", "Nombre de Almacén", "Nombre", "C. interno", "C. barras", "Precio venta", "Stock" } };
    all.AddRange(productRows);
    var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
    for (var r = 0; r < all.Count; r++)
    {
        sb.Append("<row r=\"").Append(r + 1).Append("\">");
        for (var c = 0; c < all[r].Length; c++)
        {
            var cell = $"{Column(c)}{r + 1}";
            sb.Append("<c r=\"").Append(cell).Append("\" t=\"inlineStr\"><is><t>")
              .Append(System.Security.SecurityElement.Escape(all[r][c]))
              .Append("</t></is></c>");
        }
        sb.Append("</row>");
    }
    sb.Append("</sheetData></worksheet>");
    Write(zip, "xl/worksheets/sheet1.xml", sb.ToString());
    return path;
}

static string Column(int index)
{
    var value = index + 1;
    var result = "";
    while (value > 0)
    {
        value--;
        result = (char)('A' + value % 26) + result;
        value /= 26;
    }
    return result;
}

static void Write(ZipArchive zip, string name, string content)
{
    var entry = zip.CreateEntry(name);
    using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
    writer.Write(content);
}

static HttpResponseMessage Json(HttpStatusCode status, string body)
    => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

static T Single<T>(IEnumerable<T> source, Func<T, bool> predicate)
{
    var values = source.Where(predicate).ToArray();
    if (values.Length != 1) throw new Exception($"Se esperaba 1 elemento y se obtuvieron {values.Length}.");
    return values[0];
}

static void Eq<T>(T expected, T actual, string label)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"{label}: esperado={expected}, actual={actual}");
}

static void True(bool condition, string label)
{
    if (!condition) throw new Exception(label);
}

static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new Exception($"Se esperaba {typeof(T).Name}.");
}

sealed class QueueHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responders;

    public QueueHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responders)
        => _responders = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>(responders);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_responders.Count == 0) throw new InvalidOperationException("No quedan respuestas fake.");
        return Task.FromResult(_responders.Dequeue()(request));
    }
}
