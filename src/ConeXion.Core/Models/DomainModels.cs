using System.Text.Json.Serialization;

namespace ConeXion.Core.Models;

public sealed class CatalogDocument
{
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; set; }
    [JsonPropertyName("catalog_version")] public int CatalogVersion { get; set; }
    [JsonPropertyName("generated_at")] public string GeneratedAt { get; set; } = "";
    [JsonPropertyName("productos")] public List<CatalogProduct> Productos { get; set; } = [];
    [JsonPropertyName("excluidos")] public List<CatalogExcluded> Excluidos { get; set; } = [];
}

public sealed class CatalogProduct
{
    [JsonPropertyName("c_interno")] public int CInterno { get; set; }
    [JsonPropertyName("producto")] public string Producto { get; set; } = "";
    [JsonPropertyName("c_barras")] public string? CBarras { get; set; }
    [JsonPropertyName("precio")] public decimal Precio { get; set; }
}

public sealed class CatalogExcluded
{
    [JsonPropertyName("c_interno")] public int CInterno { get; set; }
    [JsonPropertyName("producto")] public string Producto { get; set; } = "";
}

public sealed class ExcelProductRow
{
    public int RowNumber { get; init; }
    public string StoreName { get; init; } = "";
    public string WarehouseName { get; init; } = "";
    public string ProductName { get; init; } = "";
    public string InternalCodeRaw { get; init; } = "";
    public string Barcode { get; init; } = "";
    public string PriceRaw { get; init; } = "";
    public string StockRaw { get; init; } = "";
}

public sealed class SnapshotDocument
{
    [JsonPropertyName("contract_version")] public int ContractVersion { get; set; } = 2;
    [JsonPropertyName("snapshot_id")] public Guid SnapshotId { get; set; }
    [JsonPropertyName("capturado_at")] public string CapturadoAt { get; set; } = "";
    [JsonPropertyName("version_catalogo")] public int VersionCatalogo { get; set; }
    [JsonPropertyName("excel_hash")] public string ExcelHash { get; set; } = "";
    [JsonPropertyName("stock")] public List<StockItem> Stock { get; set; } = [];
    [JsonPropertyName("eliminados")] public List<DeletedProduct> Eliminados { get; set; } = [];
    [JsonPropertyName("incidencias")] public List<Incident> Incidencias { get; set; } = [];
    [JsonPropertyName("resumen")] public SnapshotSummary Resumen { get; set; } = new();

    [JsonIgnore] public string StoreName { get; set; } = "";
    [JsonIgnore] public string SourceFileName { get; set; } = "";
}

public sealed class SnapshotSummary
{
    [JsonPropertyName("sku_total_excel")] public int SkuTotalExcel { get; set; }
    [JsonPropertyName("sku_catalogo")] public int SkuCatalogo { get; set; }
    [JsonPropertyName("sku_excluidos")] public int SkuExcluidos { get; set; }
    [JsonPropertyName("sku_validos")] public int SkuValidos { get; set; }
    [JsonPropertyName("sku_stock_no_cero")] public int SkuStockNoCero { get; set; }
    [JsonPropertyName("sku_stock_cero")] public int SkuStockCero { get; set; }
    [JsonPropertyName("sku_eliminados")] public int SkuEliminados { get; set; }
    [JsonPropertyName("incidencias_total")] public int IncidenciasTotal { get; set; }
}

public sealed class StockItem
{
    [JsonPropertyName("c_interno")] public int CInterno { get; set; }
    [JsonPropertyName("stock")] public int Stock { get; set; }
}

public sealed class DeletedProduct
{
    [JsonPropertyName("c_interno")] public int CInterno { get; set; }
    [JsonPropertyName("motivo")] public string Motivo { get; set; } = "";
}

public sealed class Incident
{
    [JsonPropertyName("c_interno")] public int? CInterno { get; set; }
    [JsonPropertyName("c_interno_original")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? CInternoOriginal { get; set; }
    [JsonPropertyName("tipo")] public string Tipo { get; set; } = "";
    [JsonPropertyName("datos")] public Dictionary<string, object?> Datos { get; set; } = [];
}

public sealed class ProcessingResult
{
    public required SnapshotDocument Snapshot { get; init; }
}

public sealed class InstallationConfig
{
    public string InstallationId { get; set; } = "";
    public string StoreId { get; set; } = "";
    public string StoreName { get; set; } = "";
    public int CatalogVersion { get; set; }
    public string CatalogSha256 { get; set; } = "";
    public bool Provisioned { get; set; }
}

public enum LocalSnapshotStatus { Pending, Synced, Failed }

public sealed class LocalSnapshotRecord
{
    public Guid SnapshotId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string StoreName { get; set; } = "";
    public string SourceFileName { get; set; } = "";
    public string SourceHash { get; set; } = "";
    public int CatalogVersion { get; set; }
    public LocalSnapshotStatus Status { get; set; }
    public string PayloadJson { get; set; } = "";
    public int IncidentCount { get; set; }
    public string? LastError { get; set; }
}

public enum SyncTrigger { Startup, BeforeNewImport, ImmediateAfterImport }
