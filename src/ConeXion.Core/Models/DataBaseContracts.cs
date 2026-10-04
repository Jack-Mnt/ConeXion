namespace ConeXion.Core.Models;

public sealed record InstallationCredentials(string InstallationId, string InstallationSecret);

public sealed class AuthState
{
    public bool Ok { get; set; }
    public string Codigo { get; set; } = "";
    public string InstallationStatus { get; set; } = "";
    public string StoreId { get; set; } = "";
    public string StoreName { get; set; } = "";
    public int? InstalledCatalogVersion { get; set; }
    public int CurrentCatalogVersion { get; set; }
    public int CatalogSchemaVersion { get; set; }
    public string CatalogSha256 { get; set; } = "";
    public string CatalogStoragePath { get; set; } = "";
    public long CatalogSizeBytes { get; set; }
    public bool CatalogUpdateRequired { get; set; }
    public DateTimeOffset? LastConfirmedSnapshotAt { get; set; }
}

public sealed record CatalogDownloadResult(
    bool Accepted,
    byte[]? Package,
    int Version,
    int SchemaVersion,
    string Sha256,
    long SizeBytes,
    string? Error);

public sealed record SnapshotUploadResponse(
    bool Accepted,
    bool Duplicate,
    Guid? SnapshotId,
    DateTimeOffset? ConfirmedAt,
    DateTimeOffset? LastSnapshotCapturedAt,
    DateTimeOffset? SnapshotExpiresAt,
    DateTimeOffset? ServerNow,
    int StockRows,
    int IncidentsProcessed,
    string? Codigo,
    string? Error,
    bool Retryable);

public sealed record DataBaseHealth(bool Available, string? Message);
