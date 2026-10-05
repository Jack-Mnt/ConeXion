using ConeXion.Core.Models;

namespace ConeXion.Core.Services;

public sealed class SyncCoordinator
{
    private readonly IDataBaseGateway _gateway;
    private readonly LocalDatabase _db;
    private readonly CatalogService _catalogs;
    private readonly ConfigurationService _config;
    private readonly CredentialStore _credentials;

    public SyncCoordinator(IDataBaseGateway gateway, LocalDatabase db, CatalogService catalogs, ConfigurationService config, CredentialStore credentials)
    { _gateway = gateway; _db = db; _catalogs = catalogs; _config = config; _credentials = credentials; }

    public async Task<SyncRunResult> RunAsync(SyncTrigger trigger, string appVersion, CancellationToken ct = default)
    {
        var cfg = _config.Load();
        if (cfg is null || !cfg.Provisioned || !_credentials.Exists)
            return new(false, 0, false, null, null, null, null, "Esta computadora no está configurada.", false);

        var cachedCaptured = Parse(await _db.GetStateAsync("last_snapshot_captured_at", ct));
        var cachedExpires = Parse(await _db.GetStateAsync("snapshot_expires_at", ct));
        var cachedConfirmed = Parse(await _db.GetStateAsync("last_confirmed_snapshot", ct));

        var creds = _credentials.Load(cfg);
        CatalogDocument? local = null;
        string? localHash = null;
        if (_catalogs.Exists)
        {
            try
            {
                local = _catalogs.LoadCurrent();
                localHash = _catalogs.CurrentPackageSha256();
            }
            catch
            {
                local = null;
                localHash = null;
            }
        }

        AuthState state;
        try
        {
            state = await _gateway.GetStateAsync(creds, appVersion, local?.CatalogVersion, localHash, ct);
        }
        catch (SupabaseGatewayException ex)
        {
            var transient = ex.HttpStatus >= 500 || ex.HttpStatus is 408 or 429;
            return new(!transient, 0, false, cachedCaptured, cachedExpires, null, cachedConfirmed,
                BuildAuthError(ex.Codigo), !transient);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new(false, 0, false, cachedCaptured, cachedExpires, null, cachedConfirmed, "No se pudo conectar con Supabase.", false);
        }

        if (!state.Ok)
            return new(false, 0, false, cachedCaptured, cachedExpires, null, state.LastConfirmedSnapshotAt ?? cachedConfirmed, state.Codigo, false);

        if (!string.Equals(state.StoreName, cfg.StoreName, StringComparison.OrdinalIgnoreCase))
            return new(false, 0, false, cachedCaptured, cachedExpires, null, state.LastConfirmedSnapshotAt ?? cachedConfirmed, "La sede devuelta por Supabase no coincide con la configuración local.", false);

        if (state.CatalogSchemaVersion != CatalogService.SupportedSchemaVersion)
            return new(true, 0, false, cachedCaptured, cachedExpires, null, state.LastConfirmedSnapshotAt ?? cachedConfirmed,
                $"Schema de catálogo no compatible: {state.CatalogSchemaVersion}. ConeXion requiere schema {CatalogService.SupportedSchemaVersion}.", true);

        var catalogUpdated = false;
        if (state.CatalogUpdateRequired || local is null || local.CatalogVersion != state.CurrentCatalogVersion || !string.Equals(localHash ?? "", state.CatalogSha256, StringComparison.OrdinalIgnoreCase))
        {
            var download = await _gateway.DownloadCatalogAsync(creds, appVersion, local?.CatalogVersion, localHash, ct);
            if (!download.Accepted || download.Package is null)
                return new(true, 0, false, cachedCaptured, cachedExpires, null, state.LastConfirmedSnapshotAt ?? cachedConfirmed, download.Error ?? "No se pudo descargar el catálogo.", false);

            try
            {
                if (download.SchemaVersion != CatalogService.SupportedSchemaVersion)
                    throw new InvalidDataException($"Schema de catálogo no compatible: {download.SchemaVersion}.");
                _catalogs.InstallPackageAtomically(download.Package, download.Sha256, download.SizeBytes, download.Version, download.SchemaVersion);
                try
                {
                    if (!await _gateway.ConfirmCatalogAsync(creds, appVersion, download.Version, download.Sha256, ct))
                        throw new InvalidDataException("Supabase no confirmó la instalación del catálogo.");
                }
                catch
                {
                    _catalogs.RollbackUnconfirmedInstall();
                    throw;
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidDataException or HttpRequestException or TaskCanceledException)
            {
                return new(true, 0, false, cachedCaptured, cachedExpires, null, state.LastConfirmedSnapshotAt ?? cachedConfirmed,
                    $"No se pudo actualizar el catálogo. {(local is null ? "No existe una versión V2 local utilizable." : "Se conservó la versión V2 anterior.")} {ex.Message}", false);
            }

            cfg.CatalogVersion = download.Version;
            cfg.CatalogSha256 = download.Sha256;
            _config.Save(cfg);
            local = _catalogs.LoadCurrent();
            localHash = _catalogs.CurrentPackageSha256();
            catalogUpdated = true;
        }

        var lastCaptured = cachedCaptured;
        var expiresAt = cachedExpires;
        var serverNow = (DateTimeOffset?)null;
        var lastConfirmed = state.LastConfirmedSnapshotAt ?? cachedConfirmed;
        await PersistStateAsync(lastCaptured, expiresAt, lastConfirmed, ct);

        var sent = 0;
        var permanentFailure = false;
        string? message = null;

        foreach (var pending in await _db.GetPendingAsync(ct))
        {
            var snapshot = SnapshotExporter.Deserialize(pending.PayloadJson);
            if (snapshot.ContractVersion != 2)
            {
                await _db.MarkStatusAsync(snapshot.SnapshotId, LocalSnapshotStatus.Failed, "INVALID_CONTRACT_VERSION", ct);
                permanentFailure = true;
                message = "Existe un snapshot local incompatible con contrato V2. Revisa Historial.";
                break;
            }

            var reply = await _gateway.UploadSnapshotAsync(creds, appVersion, snapshot, ct);
            if (!reply.Accepted && !reply.Duplicate)
            {
                if (reply.Retryable)
                {
                    await _db.MarkStatusAsync(snapshot.SnapshotId, LocalSnapshotStatus.Pending, reply.Error ?? reply.Codigo, ct);
                    message = reply.Error ?? reply.Codigo;
                }
                else
                {
                    await _db.MarkStatusAsync(snapshot.SnapshotId, LocalSnapshotStatus.Failed, reply.Error ?? reply.Codigo, ct);
                    permanentFailure = true;
                    message = "Existe un snapshot rechazado por el backend. Revisa Historial.";
                }
                break;
            }

            await _db.MarkStatusAsync(snapshot.SnapshotId, LocalSnapshotStatus.Synced, null, ct);
            lastConfirmed = reply.ConfirmedAt ?? lastConfirmed;
            lastCaptured = reply.LastSnapshotCapturedAt ?? Parse(snapshot.CapturadoAt) ?? lastCaptured;
            expiresAt = reply.SnapshotExpiresAt ?? (lastCaptured.HasValue ? lastCaptured.Value.AddHours(2) : expiresAt);
            serverNow = reply.ServerNow ?? serverNow;
            await PersistStateAsync(lastCaptured, expiresAt, lastConfirmed, ct);
            sent++;
        }

        return new(true, sent, catalogUpdated, lastCaptured, expiresAt, serverNow, lastConfirmed, message, permanentFailure);
    }

    private static string BuildAuthError(string codigo)
        => codigo switch
        {
            "INVALID_CREDENTIALS" => "Las credenciales de esta instalación no son válidas.",
            "INSTALLATION_DISABLED" => "Esta instalación está deshabilitada en Supabase.",
            "CATALOG_SCHEMA_NOT_SUPPORTED" => "El schema de catálogo publicado no es compatible con ConeXion 2.0.2.",
            "CATALOG_VERSION_NOT_AVAILABLE" => "Supabase no tiene disponible la versión de catálogo requerida.",
            "INTERNAL_ERROR" => "Supabase no pudo completar la consulta de estado.",
            _ => codigo
        };

    private async Task PersistStateAsync(DateTimeOffset? capturedAt, DateTimeOffset? expiresAt, DateTimeOffset? confirmedAt, CancellationToken ct)
    {
        if (capturedAt.HasValue) await _db.SetStateAsync("last_snapshot_captured_at", capturedAt.Value.ToString("O"), ct);
        if (expiresAt.HasValue) await _db.SetStateAsync("snapshot_expires_at", expiresAt.Value.ToString("O"), ct);
        if (confirmedAt.HasValue) await _db.SetStateAsync("last_confirmed_snapshot", confirmedAt.Value.ToString("O"), ct);
    }

    private static DateTimeOffset? Parse(string? value) => DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
}

public sealed record SyncRunResult(
    bool DataBaseAvailable,
    int SnapshotsSent,
    bool CatalogUpdated,
    DateTimeOffset? LastSnapshotCapturedAt,
    DateTimeOffset? SnapshotExpiresAt,
    DateTimeOffset? ServerNow,
    DateTimeOffset? LastConfirmedSnapshotAt,
    string? Message,
    bool PermanentFailure);
