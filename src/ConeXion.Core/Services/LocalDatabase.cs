using Microsoft.Data.Sqlite;
using ConeXion.Core.Models;

namespace ConeXion.Core.Services;

public sealed class LocalDatabase
{
    private readonly string _connectionString;
    private readonly AppPaths _paths;

    public LocalDatabase(AppPaths paths)
    {
        _paths = paths;
        _connectionString = new SqliteConnectionStringBuilder { DataSource = paths.DatabaseFile, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        _paths.EnsureRuntimeDirectories();
        await using var cn = new SqliteConnection(_connectionString);
        await cn.OpenAsync(ct);
        var sql = """
        PRAGMA journal_mode=WAL;
        CREATE TABLE IF NOT EXISTS snapshots (
            snapshot_id TEXT PRIMARY KEY,
            created_at TEXT NOT NULL,
            store_name TEXT NOT NULL,
            source_file_name TEXT NOT NULL,
            source_hash TEXT NOT NULL,
            catalog_version INTEGER NOT NULL,
            status TEXT NOT NULL,
            incident_count INTEGER NOT NULL,
            payload_json TEXT NOT NULL,
            last_error TEXT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ux_snapshots_hash_store ON snapshots(source_hash, store_name);
        CREATE TABLE IF NOT EXISTS app_state (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
        """;
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> SnapshotHashExistsAsync(string storeName, string hash, CancellationToken ct = default)
    {
        await using var cn = new SqliteConnection(_connectionString);
        await cn.OpenAsync(ct);
        await using var cmd = cn.CreateCommand();
        // Un snapshot rechazado permanentemente no bloquea volver a procesar el mismo
        // Excel después de corregir ConeXion. Pending/Synced sí conservan la protección
        // local contra duplicados.
        cmd.CommandText = "SELECT 1 FROM snapshots WHERE store_name=$store AND source_hash=$hash AND status IN ('Pending','Synced') LIMIT 1";
        cmd.Parameters.AddWithValue("$store", storeName);
        cmd.Parameters.AddWithValue("$hash", hash);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    public async Task SaveSnapshotAsync(SnapshotDocument snapshot, LocalSnapshotStatus status, CancellationToken ct = default)
    {
        var payload = SnapshotExporter.SerializeCompact(snapshot);
        await using var cn = new SqliteConnection(_connectionString);
        await cn.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);

        // Si el mismo Excel había quedado Failed, el backend nunca lo confirmó.
        // Retiramos únicamente ese intento local para permitir reconstruir el payload
        // corregido manteniendo la protección para Pending/Synced.
        await using (var cleanup = cn.CreateCommand())
        {
            cleanup.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)tx;
            cleanup.CommandText = "DELETE FROM snapshots WHERE store_name=$store AND source_hash=$hash AND status='Failed'";
            cleanup.Parameters.AddWithValue("$store", snapshot.StoreName);
            cleanup.Parameters.AddWithValue("$hash", snapshot.ExcelHash);
            await cleanup.ExecuteNonQueryAsync(ct);
        }

        await using var cmd = cn.CreateCommand();
        cmd.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)tx;
        cmd.CommandText = """
        INSERT INTO snapshots(snapshot_id, created_at, store_name, source_file_name, source_hash, catalog_version, status, incident_count, payload_json)
        VALUES($id,$created,$store,$file,$hash,$catalog,$status,$incidents,$payload)
        ON CONFLICT(snapshot_id) DO UPDATE SET status=excluded.status, payload_json=excluded.payload_json, incident_count=excluded.incident_count
        """;
        cmd.Parameters.AddWithValue("$id", snapshot.SnapshotId.ToString());
        cmd.Parameters.AddWithValue("$created", snapshot.CapturadoAt);
        cmd.Parameters.AddWithValue("$store", snapshot.StoreName);
        cmd.Parameters.AddWithValue("$file", snapshot.SourceFileName);
        cmd.Parameters.AddWithValue("$hash", snapshot.ExcelHash);
        cmd.Parameters.AddWithValue("$catalog", snapshot.VersionCatalogo);
        cmd.Parameters.AddWithValue("$status", status.ToString());
        cmd.Parameters.AddWithValue("$incidents", snapshot.Incidencias.Count);
        cmd.Parameters.AddWithValue("$payload", payload);
        await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task<DateTimeOffset?> GetLatestPendingCapturedAtAsync(CancellationToken ct = default)
    {
        await using var cn = new SqliteConnection(_connectionString);
        await cn.OpenAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT MAX(created_at) FROM snapshots WHERE status='Pending'";
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is string text && DateTimeOffset.TryParse(text, out var parsed) ? parsed : null;
    }

    public async Task<DateTimeOffset?> GetLatestSyncedCapturedAtAsync(CancellationToken ct = default)
    {
        await using var cn = new SqliteConnection(_connectionString);
        await cn.OpenAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT MAX(created_at) FROM snapshots WHERE status='Synced'";
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is string text && DateTimeOffset.TryParse(text, out var parsed) ? parsed : null;
    }

    public async Task<List<LocalSnapshotRecord>> GetPendingAsync(CancellationToken ct = default)
    {
        var result = new List<LocalSnapshotRecord>();
        await using var cn = new SqliteConnection(_connectionString);
        await cn.OpenAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT snapshot_id,created_at,store_name,source_file_name,source_hash,catalog_version,status,incident_count,payload_json,last_error FROM snapshots WHERE status='Pending' ORDER BY created_at ASC";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new LocalSnapshotRecord
            {
                SnapshotId = Guid.Parse(reader.GetString(0)), CreatedAt = DateTimeOffset.Parse(reader.GetString(1)), StoreName = reader.GetString(2),
                SourceFileName = reader.GetString(3), SourceHash = reader.GetString(4), CatalogVersion = reader.GetInt32(5),
                Status = Enum.Parse<LocalSnapshotStatus>(reader.GetString(6)), IncidentCount = reader.GetInt32(7), PayloadJson = reader.GetString(8), LastError = reader.IsDBNull(9) ? null : reader.GetString(9)
            });
        }
        return result;
    }

    public async Task MarkStatusAsync(Guid id, LocalSnapshotStatus status, string? error = null, CancellationToken ct = default)
    {
        await using var cn = new SqliteConnection(_connectionString);
        await cn.OpenAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "UPDATE snapshots SET status=$status,last_error=$error WHERE snapshot_id=$id";
        cmd.Parameters.AddWithValue("$status", status.ToString());
        cmd.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", id.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<LocalSnapshotRecord>> GetHistoryAsync(int limit = 50, CancellationToken ct = default)
    {
        var result = new List<LocalSnapshotRecord>();
        await using var cn = new SqliteConnection(_connectionString);
        await cn.OpenAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT snapshot_id,created_at,store_name,source_file_name,source_hash,catalog_version,status,incident_count,payload_json,last_error FROM snapshots ORDER BY created_at DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", limit);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new LocalSnapshotRecord
            {
                SnapshotId = Guid.Parse(reader.GetString(0)), CreatedAt = DateTimeOffset.Parse(reader.GetString(1)), StoreName = reader.GetString(2),
                SourceFileName = reader.GetString(3), SourceHash = reader.GetString(4), CatalogVersion = reader.GetInt32(5),
                Status = Enum.Parse<LocalSnapshotStatus>(reader.GetString(6)), IncidentCount = reader.GetInt32(7), PayloadJson = reader.GetString(8), LastError = reader.IsDBNull(9) ? null : reader.GetString(9)
            });
        }
        return result;
    }

    public async Task SetStateAsync(string key, string value, CancellationToken ct = default)
    {
        await using var cn = new SqliteConnection(_connectionString);
        await cn.OpenAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "INSERT INTO app_state(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        cmd.Parameters.AddWithValue("$key", key); cmd.Parameters.AddWithValue("$value", value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> GetStateAsync(string key, CancellationToken ct = default)
    {
        await using var cn = new SqliteConnection(_connectionString);
        await cn.OpenAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT value FROM app_state WHERE key=$key"; cmd.Parameters.AddWithValue("$key", key);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }
}
