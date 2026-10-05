using System.Net;
using System.Text;
using System.Text.Json;
using ConeXion.Core.Models;

namespace ConeXion.Core.Services;

public sealed class SupabaseGateway : IDataBaseGateway, IDisposable
{
    public const string DefaultBaseUrl = "https://fvtohxvcvsflzmftgfzs.supabase.co";
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public SupabaseGateway(string? baseUrl = null, HttpMessageHandler? handler = null)
    {
        _baseUrl = (baseUrl ?? DefaultBaseUrl).TrimEnd('/');
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(20);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("ConeXion/2.0.2");
    }

    public async Task<DataBaseHealth> CheckHealthAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/functions/v1/conexion-auth");
            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            return new DataBaseHealth(
                res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.OK or HttpStatusCode.NoContent,
                $"HTTP {(int)res.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new DataBaseHealth(false, "Sin conexión con Supabase.");
        }
    }

    public async Task<AuthState> GetStateAsync(InstallationCredentials credentials, string appVersion, int? localCatalogVersion, string? localCatalogSha256, CancellationToken ct = default)
    {
        var body = new
        {
            action = "state",
            installation_id = credentials.InstallationId,
            installation_secret = credentials.InstallationSecret,
            conexion_version = appVersion,
            catalog_local_version = localCatalogVersion,
            catalog_local_hash = localCatalogSha256
        };

        using var res = await SendJsonAsync("conexion-auth", body, credentials, appVersion, ct);
        var json = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new SupabaseGatewayException(ReadCodigo(json) ?? $"AUTH_HTTP_{(int)res.StatusCode}", (int)res.StatusCode);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new AuthState
        {
            Ok = GetBool(root, "ok"),
            Codigo = GetString(root, "codigo"),
            InstallationStatus = GetString(root, "installation_estado"),
            StoreId = GetString(root, "sede_id"),
            StoreName = GetString(root, "sede_nombre"),
            InstalledCatalogVersion = GetNullableInt(root, "installation_catalog_version"),
            CurrentCatalogVersion = GetInt(root, "catalog_version"),
            CatalogSchemaVersion = GetInt(root, "catalog_schema_version"),
            CatalogSha256 = GetString(root, "catalog_hash"),
            CatalogStoragePath = GetString(root, "catalog_storage_path"),
            CatalogSizeBytes = GetLong(root, "catalog_size"),
            CatalogUpdateRequired = GetBool(root, "catalog_update_required"),
            LastConfirmedSnapshotAt = GetDate(root, "ultimo_snapshot_confirmado_at")
        };
    }

    public async Task<CatalogDownloadResult> DownloadCatalogAsync(InstallationCredentials credentials, string appVersion, int? localCatalogVersion, string? localCatalogSha256, CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/functions/v1/conexion-auth?catalog_version={(localCatalogVersion ?? 0)}&catalog_sha256={Uri.EscapeDataString(localCatalogSha256 ?? "")}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        AddHeaders(req, credentials, appVersion);
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

        if (res.StatusCode == HttpStatusCode.NoContent)
            return new CatalogDownloadResult(true, null, HeaderInt(res, "x-catalog-version"), HeaderInt(res, "x-catalog-schema-version"), Header(res, "x-catalog-sha256"), HeaderLong(res, "x-catalog-size"), null);

        if (!res.IsSuccessStatusCode)
        {
            var error = await res.Content.ReadAsStringAsync(ct);
            return new CatalogDownloadResult(false, null, 0, 0, "", 0, ReadCodigo(error) ?? $"HTTP_{(int)res.StatusCode}");
        }

        var bytes = await res.Content.ReadAsByteArrayAsync(ct);
        return new CatalogDownloadResult(true, bytes, HeaderInt(res, "x-catalog-version"), HeaderInt(res, "x-catalog-schema-version"), Header(res, "x-catalog-sha256"), HeaderLong(res, "x-catalog-size"), null);
    }

    public async Task<bool> ConfirmCatalogAsync(InstallationCredentials credentials, string appVersion, int version, string sha256, CancellationToken ct = default)
    {
        var body = new
        {
            action = "confirm_catalog",
            installation_id = credentials.InstallationId,
            installation_secret = credentials.InstallationSecret,
            conexion_version = appVersion,
            confirm_catalog_version = version,
            confirm_catalog_hash = sha256
        };
        using var res = await SendJsonAsync("conexion-auth", body, credentials, appVersion, ct);
        if (!res.IsSuccessStatusCode) return false;
        var json = await res.Content.ReadAsStringAsync(ct);
        using var doc = TryParse(json);
        if (doc is null) return false;
        var root = doc.RootElement;
        return GetBool(root, "ok") && string.Equals(GetString(root, "codigo"), "OK", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<SnapshotUploadResponse> UploadSnapshotAsync(InstallationCredentials credentials, string appVersion, SnapshotDocument snapshot, CancellationToken ct = default)
    {
        try
        {
            using var res = await SendJsonAsync("conexion-sync", snapshot, credentials, appVersion, ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            var codigo = ReadCodigo(text) ?? $"HTTP_{(int)res.StatusCode}";

            using var doc = TryParse(text);
            var root = doc?.RootElement;
            var confirmed = root.HasValue ? GetDate(root.Value, "ultimo_snapshot_confirmado_at") : null;
            var captured = root.HasValue ? GetDate(root.Value, "ultimo_snapshot_capturado_at") : null;
            var expires = root.HasValue ? GetDate(root.Value, "snapshot_expira_at") ?? GetDate(root.Value, "next_snapshot_allowed_at") : null;
            var serverNow = root.HasValue ? GetDate(root.Value, "server_now") : null;

            if (!res.IsSuccessStatusCode)
            {
                var retryableCode = codigo is "SNAPSHOT_WINDOW_NOT_OPEN" or "SNAPSHOT_CAPTURE_TIME_IN_FUTURE";
                var retryableHttp = (int)res.StatusCode >= 500 || res.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;
                return new SnapshotUploadResponse(false, false, snapshot.SnapshotId, confirmed, captured, expires, serverNow, 0, 0, codigo, BuildSyncError(codigo, expires), retryableCode || retryableHttp);
            }

            var ok = root.HasValue && GetBool(root.Value, "ok");
            var duplicate = root.HasValue && (GetBool(root.Value, "duplicado") || string.Equals(GetString(root.Value, "codigo"), "DUPLICATE", StringComparison.OrdinalIgnoreCase));
            Guid? sid = snapshot.SnapshotId;
            if (root.HasValue && root.Value.TryGetProperty("snapshot_id", out var sidEl) && sidEl.ValueKind == JsonValueKind.String && Guid.TryParse(sidEl.GetString(), out var parsed))
                sid = parsed;

            return new SnapshotUploadResponse(
                ok,
                duplicate,
                sid,
                confirmed,
                captured,
                expires,
                serverNow,
                root.HasValue ? GetInt(root.Value, "stock_rows") : 0,
                root.HasValue ? GetInt(root.Value, "incidencias_procesadas") : 0,
                root.HasValue ? GetString(root.Value, "codigo") : codigo,
                null,
                false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new SnapshotUploadResponse(false, false, snapshot.SnapshotId, null, null, null, null, 0, 0, "NETWORK_ERROR", "No se pudo conectar con Supabase.", true);
        }
    }

    private static string BuildSyncError(string codigo, DateTimeOffset? nextAllowedAt)
    {
        return codigo switch
        {
            "SNAPSHOT_INTERVAL_TOO_SHORT" => nextAllowedAt.HasValue
                ? $"Este inventario fue capturado antes del intervalo mínimo permitido. Próxima captura válida desde {nextAllowedAt.Value.ToLocalTime():HH:mm}."
                : "Este inventario fue capturado antes del intervalo mínimo de 2 horas.",
            "SNAPSHOT_WINDOW_NOT_OPEN" => nextAllowedAt.HasValue
                ? $"La ventana de actualización todavía no está habilitada. Disponible desde {nextAllowedAt.Value.ToLocalTime():HH:mm}."
                : "La ventana de actualización todavía no está habilitada.",
            "SNAPSHOT_CAPTURE_TIME_IN_FUTURE" => "La hora de captura del inventario está adelantada respecto al servidor. Verifica la fecha y hora de Windows.",
            "OUT_OF_ORDER_SNAPSHOT" => "Existe un snapshot posterior ya confirmado para esta sede.",
            "INVALID_CONTRACT_VERSION" => "La versión del contrato de ConeXion no es compatible con Supabase.",
            "CATALOG_SCHEMA_NOT_SUPPORTED" => "El schema del catálogo no es compatible con esta versión de ConeXion.",
            "CATALOG_VERSION_NOT_AVAILABLE" => "La versión del catálogo usada por el inventario ya no está disponible en Supabase.",
            "INVALID_CREDENTIALS" => "Las credenciales de esta instalación no son válidas.",
            "INSTALLATION_DISABLED" => "Esta instalación está deshabilitada en Supabase.",
            "INVALID_PAYLOAD" or "INVALID_PAYLOAD_METADATA" or "INVALID_PAYLOAD_STRUCTURE" or "INVALID_SUMMARY" or "INVALID_SKU_ARRAY" or "DUPLICATE_SKU_IN_STOCK" or "DUPLICATE_SKU_IN_PAYLOAD" or "SKU_STATE_CONFLICT" or "UNKNOWN_SKU_WITHOUT_INCIDENT" or "ELIMINATED_WITHOUT_INCIDENT" or "INCIDENT_INVALID" => $"Supabase rechazó el snapshot por contrato V2: {codigo}.",
            _ => codigo
        };
    }

    private async Task<HttpResponseMessage> SendJsonAsync(string function, object body, InstallationCredentials credentials, string appVersion, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/functions/v1/{function}");
        AddHeaders(req, credentials, appVersion);
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private static void AddHeaders(HttpRequestMessage req, InstallationCredentials credentials, string appVersion)
    {
        req.Headers.TryAddWithoutValidation("x-installation-id", credentials.InstallationId);
        req.Headers.TryAddWithoutValidation("x-installation-secret", credentials.InstallationSecret);
        req.Headers.TryAddWithoutValidation("x-conexion-version", appVersion);
    }

    private static JsonDocument? TryParse(string json) { try { return JsonDocument.Parse(json); } catch { return null; } }
    private static string Header(HttpResponseMessage r, string name) => r.Headers.TryGetValues(name, out var v) ? v.FirstOrDefault() ?? "" : "";
    private static int HeaderInt(HttpResponseMessage r, string name) => int.TryParse(Header(r, name), out var x) ? x : 0;
    private static long HeaderLong(HttpResponseMessage r, string name) => long.TryParse(Header(r, name), out var x) ? x : 0;
    private static string GetString(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static int GetInt(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var x) ? x : 0;
    private static int? GetNullableInt(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var x) ? x : null;
    private static long GetLong(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var x) ? x : 0;
    private static bool GetBool(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.True;
    private static DateTimeOffset? GetDate(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(v.GetString(), out var d) ? d : null;
    private static string? ReadCodigo(string json) { try { using var d = JsonDocument.Parse(json); return GetString(d.RootElement, "codigo"); } catch { return null; } }

    public void Dispose() => _http.Dispose();
}

public sealed class SupabaseGatewayException : Exception
{
    public string Codigo { get; }
    public int HttpStatus { get; }
    public SupabaseGatewayException(string codigo, int httpStatus) : base(codigo) { Codigo = codigo; HttpStatus = httpStatus; }
}
