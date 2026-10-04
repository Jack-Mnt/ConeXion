namespace ConeXion.Core.Services;

public sealed class AppPaths
{
    public string Root { get; }
    public string Data { get; }
    public string Catalog { get; }
    public string Runtime { get; }
    public string Logs { get; }
    public string Output { get; }
    public string CatalogPackage { get; }
    public string PreviousCatalogPackage { get; }
    public string ConfigFile { get; }
    public string CredentialsFile { get; }
    public string DatabaseFile { get; }

    private AppPaths(string root)
    {
        Root = root;
        Data = Path.Combine(root, "Data");
        Catalog = Path.Combine(root, "Catalog");
        Runtime = Path.Combine(root, "Runtime");
        Logs = Path.Combine(Runtime, "Logs");
        Output = Path.Combine(Runtime, "Salida");
        CatalogPackage = Path.Combine(Catalog, "catalog.prcatalog");
        PreviousCatalogPackage = Path.Combine(Catalog, "catalog.previous.prcatalog");
        ConfigFile = Path.Combine(Data, "config.json");
        CredentialsFile = Path.Combine(Data, "credentials.dat");
        DatabaseFile = Path.Combine(Runtime, "conexion.db");
    }

    public static AppPaths Production()
    {
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return new AppPaths(Path.Combine(common, "PuertoRico", "ConeXion"));
    }

    public void EnsureSharedDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(Catalog);
        EnsureRuntimeDirectories();
    }

    public void EnsureRuntimeDirectories()
    {
        Directory.CreateDirectory(Runtime);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Output);
    }
}
