using System.Text.Json;
using ConeXion.Core.Models;

namespace ConeXion.Core.Services;

public sealed class ConfigurationService
{
    private readonly AppPaths _paths;
    public ConfigurationService(AppPaths paths) => _paths = paths;

    public InstallationConfig? Load()
    {
        if (!File.Exists(_paths.ConfigFile)) return null;
        return JsonSerializer.Deserialize<InstallationConfig>(File.ReadAllText(_paths.ConfigFile));
    }

    public void Save(InstallationConfig config)
    {
        _paths.EnsureSharedDirectories();
        var temp = _paths.ConfigFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, _paths.ConfigFile, true);
    }

    public void Clear()
    {
        if (File.Exists(_paths.ConfigFile)) File.Delete(_paths.ConfigFile);
    }
}
