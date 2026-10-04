using ConeXion.Core.Models;

namespace ConeXion.Core.Services;

public interface IDataBaseGateway
{
    Task<DataBaseHealth> CheckHealthAsync(CancellationToken ct = default);
    Task<AuthState> GetStateAsync(InstallationCredentials credentials, string appVersion, int? localCatalogVersion, string? localCatalogSha256, CancellationToken ct = default);
    Task<CatalogDownloadResult> DownloadCatalogAsync(InstallationCredentials credentials, string appVersion, int? localCatalogVersion, string? localCatalogSha256, CancellationToken ct = default);
    Task<bool> ConfirmCatalogAsync(InstallationCredentials credentials, string appVersion, int version, string sha256, CancellationToken ct = default);
    Task<SnapshotUploadResponse> UploadSnapshotAsync(InstallationCredentials credentials, string appVersion, SnapshotDocument snapshot, CancellationToken ct = default);
}
