namespace ConeXion.Core.Services;

public sealed class ConeXionRuntime
{
    public AppPaths Paths { get; }
    public CatalogService Catalogs { get; }
    public LocalDatabase LocalDb { get; }
    public InventoryProcessor Processor { get; }
    public ConfigurationService Configuration { get; }
    public CredentialStore Credentials { get; }
    public RuntimePermissionsService Permissions { get; }
    public IDataBaseGateway DataBase { get; }
    public SyncCoordinator Sync { get; }
    public LogService Logs { get; }
    public DiagnosticService Diagnostics { get; }

    public ConeXionRuntime(AppPaths paths, IDataBaseGateway? dataBase = null)
    {
        Paths = paths;
        DataBase = dataBase ?? new SupabaseGateway();
        Catalogs = new CatalogService(paths);
        LocalDb = new LocalDatabase(paths);
        Processor = new InventoryProcessor();
        Configuration = new ConfigurationService(paths);
        Credentials = new CredentialStore(paths);
        Permissions = new RuntimePermissionsService(paths);
        Sync = new SyncCoordinator(DataBase, LocalDb, Catalogs, Configuration, Credentials);
        Logs = new LogService(paths);
        Diagnostics = new DiagnosticService(paths, Catalogs, LocalDb, Credentials, DataBase);
    }

    public Task InitializeAsync(CancellationToken ct = default) => LocalDb.InitializeAsync(ct);
}
