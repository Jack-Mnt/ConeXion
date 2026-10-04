using ConeXion.Core.Models;

namespace ConeXion.Core.Services;

public sealed class DiagnosticService
{
    private readonly AppPaths _paths;
    private readonly CatalogService _catalogs;
    private readonly LocalDatabase _db;
    private readonly CredentialStore _credentials;
    private readonly IDataBaseGateway _gateway;

    public DiagnosticService(AppPaths paths, CatalogService catalogs, LocalDatabase db, CredentialStore credentials, IDataBaseGateway gateway)
    { _paths = paths; _catalogs = catalogs; _db = db; _credentials = credentials; _gateway = gateway; }

    public async Task<IReadOnlyList<DiagnosticItem>> RunAsync(CancellationToken ct = default)
    {
        var items = new List<DiagnosticItem>();
        try { _paths.EnsureSharedDirectories(); items.Add(new("Archivos y permisos", true, _paths.Root)); }
        catch (Exception e) { items.Add(new("Archivos y permisos", false, e.Message)); }

        try
        {
            var c = _catalogs.LoadCurrent();
            items.Add(new("Catálogo", true, $"v{c.CatalogVersion} · schema {c.SchemaVersion} · {c.Productos.Count} productos · {c.Excluidos.Count} excluidos"));
        }
        catch (Exception e) { items.Add(new("Catálogo", false, e.Message)); }

        try { await _db.InitializeAsync(ct); items.Add(new("SQLite", true, "Correcto")); }
        catch (Exception e) { items.Add(new("SQLite", false, e.Message)); }

        items.Add(new("Credenciales", _credentials.Exists, _credentials.Exists ? "Protegidas con DPAPI LocalMachine" : "No configuradas"));
        var health = await _gateway.CheckHealthAsync(ct);
        items.Add(new("Supabase", health.Available, health.Message ?? ""));
        return items;
    }
}

public sealed record DiagnosticItem(string Name, bool Success, string Detail);
