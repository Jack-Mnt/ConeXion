namespace ConeXion.Core.Services;

public sealed class LogService
{
    private readonly AppPaths _paths;
    private const long MaxBytes = 5 * 1024 * 1024;
    private const int MaxFiles = 10;

    public LogService(AppPaths paths) => _paths = paths;

    public void Info(string message) => Write("INFO", message);
    public void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message} | {ex.GetType().Name}: {ex.Message}");

    private void Write(string level, string message)
    {
        try
        {
            _paths.EnsureRuntimeDirectories();
            Rotate();
            var file = Path.Combine(_paths.Logs, $"ConeXion-{DateTime.Now:yyyyMMdd}.log");
            File.AppendAllText(file, $"{DateTimeOffset.Now:O} [{level}] {Sanitize(message)}{Environment.NewLine}");
        }
        catch
        {
            // El registro técnico nunca debe derribar la operación principal.
        }
    }

    private void Rotate()
    {
        var files = new DirectoryInfo(_paths.Logs).GetFiles("ConeXion-*.log").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
        foreach (var extra in files.Skip(MaxFiles)) extra.Delete();
        if (files.FirstOrDefault() is { Length: > MaxBytes } current)
            current.MoveTo(Path.Combine(_paths.Logs, $"ConeXion-{DateTime.Now:yyyyMMdd-HHmmss}.log"));
    }

    private static string Sanitize(string value)
    {
        foreach (var marker in new[] { "secret=", "authorization:", "x-installation-secret" })
        {
            var idx = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0) return value[..idx] + marker + "[REDACTED]";
        }
        return value;
    }
}
