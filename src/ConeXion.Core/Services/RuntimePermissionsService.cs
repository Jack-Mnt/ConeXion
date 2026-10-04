using System.Diagnostics;

namespace ConeXion.Core.Services;

public sealed class RuntimePermissionsService
{
    private readonly AppPaths _paths;
    public RuntimePermissionsService(AppPaths paths) => _paths = paths;

    public void PrepareForStandardUsers()
    {
        _paths.EnsureSharedDirectories();
        GrantModify(_paths.Runtime, "Runtime");
        GrantModify(_paths.Catalog, "Catalog");
    }

    private static void GrantModify(string path, string label)
    {
        // SID S-1-5-32-545 = BUILTIN\Users. Usar SID evita depender del idioma de Windows.
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "icacls.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add(path);
        psi.ArgumentList.Add("/grant");
        psi.ArgumentList.Add("*S-1-5-32-545:(OI)(CI)M");
        psi.ArgumentList.Add("/T");
        psi.ArgumentList.Add("/C");

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("No se pudo iniciar icacls.exe.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"No se pudieron preparar los permisos de {label}. {stderr} {stdout}".Trim());
    }
}
