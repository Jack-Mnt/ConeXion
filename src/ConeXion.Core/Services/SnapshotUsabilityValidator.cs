using System.Globalization;
using System.Text.Json;
using ConeXion.Core.Models;

namespace ConeXion.Core.Services;

public sealed record SnapshotBlockingObservation(int? Row, string? Value);

public sealed record SnapshotUsabilityBlocker(
    int CInterno,
    string Producto,
    string Motivo,
    IReadOnlyList<SnapshotBlockingObservation> Observations);

public sealed record SnapshotUsabilityResult(
    bool IsUsable,
    IReadOnlyList<SnapshotUsabilityBlocker> Blockers)
{
    public static SnapshotUsabilityResult Usable { get; } = new(true, Array.Empty<SnapshotUsabilityBlocker>());
}

public static class SnapshotUsabilityValidator
{
    public static SnapshotUsabilityResult Validate(SnapshotDocument snapshot, CatalogDocument? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var blockers = new List<SnapshotUsabilityBlocker>();
        var catalogNames = catalog?.Productos.ToDictionary(x => x.CInterno, x => x.Producto)
            ?? new Dictionary<int, string>();

        foreach (var deleted in snapshot.Eliminados.OrderBy(x => x.CInterno))
        {
            if (TryResolveDeletedStock(deleted, out _))
                continue;

            var incident = snapshot.Incidencias.FirstOrDefault(x =>
                x.CInterno == deleted.CInterno
                && string.Equals(x.Tipo, deleted.Motivo, StringComparison.Ordinal));

            var product = catalogNames.TryGetValue(deleted.CInterno, out var catalogProduct)
                ? catalogProduct
                : ReadText(incident?.Datos, "producto") ?? $"SKU {deleted.CInterno}";

            blockers.Add(new SnapshotUsabilityBlocker(
                deleted.CInterno,
                product,
                deleted.Motivo,
                ReadObservations(incident)));
        }

        return blockers.Count == 0
            ? SnapshotUsabilityResult.Usable
            : new SnapshotUsabilityResult(false, blockers);
    }

    // La validación se expresa en términos de resolubilidad de stock, no mediante
    // una lista de tipos bloqueantes. Todo motivo nuevo de eliminados[] queda
    // bloqueado por defecto hasta que exista una semántica explícita que permita
    // resolver un stock teórico conocido.
    public static bool TryResolveDeletedStock(DeletedProduct deleted, out int stock)
    {
        ArgumentNullException.ThrowIfNull(deleted);

        if (string.Equals(deleted.Motivo, "producto_ausente", StringComparison.Ordinal))
        {
            stock = 0;
            return true;
        }

        stock = 0;
        return false;
    }

    private static IReadOnlyList<SnapshotBlockingObservation> ReadObservations(Incident? incident)
    {
        if (incident is null)
            return Array.Empty<SnapshotBlockingObservation>();

        if (string.Equals(incident.Tipo, "codigo_interno_duplicado", StringComparison.Ordinal))
        {
            var rows = ReadIntList(incident.Datos.TryGetValue("filas", out var rv) ? rv : null);
            var values = ReadTextList(incident.Datos.TryGetValue("valores_stock", out var vv) ? vv : null);
            var count = Math.Max(rows.Count, values.Count);
            var result = new List<SnapshotBlockingObservation>(count);
            for (var i = 0; i < count; i++)
                result.Add(new SnapshotBlockingObservation(
                    i < rows.Count ? rows[i] : null,
                    i < values.Count ? values[i] : null));
            return result;
        }

        if (string.Equals(incident.Tipo, "stock_invalido", StringComparison.Ordinal))
        {
            var row = ReadInt(incident.Datos.TryGetValue("fila", out var rv) ? rv : null);
            var value = ReadScalarText(incident.Datos.TryGetValue("valor_original", out var vv) ? vv : null);
            return new[] { new SnapshotBlockingObservation(row, value) };
        }

        return Array.Empty<SnapshotBlockingObservation>();
    }

    private static string? ReadText(IReadOnlyDictionary<string, object?>? data, string key)
        => data is not null && data.TryGetValue(key, out var value) ? ReadScalarText(value) : null;

    private static int? ReadInt(object? value)
    {
        if (value is null) return null;
        if (value is int i) return i;
        if (value is long l && l is >= int.MinValue and <= int.MaxValue) return (int)l;
        if (value is JsonElement json)
        {
            if (json.ValueKind == JsonValueKind.Number && json.TryGetInt32(out var n)) return n;
            if (json.ValueKind == JsonValueKind.String && int.TryParse(json.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return n;
            return null;
        }

        return int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static List<int?> ReadIntList(object? value)
    {
        if (value is JsonElement json && json.ValueKind == JsonValueKind.Array)
            return json.EnumerateArray().Select(x => ReadInt(x)).ToList();

        if (value is System.Collections.IEnumerable sequence && value is not string)
        {
            var result = new List<int?>();
            foreach (var item in sequence) result.Add(ReadInt(item));
            return result;
        }

        return value is null ? [] : [ReadInt(value)];
    }

    private static List<string?> ReadTextList(object? value)
    {
        if (value is JsonElement json && json.ValueKind == JsonValueKind.Array)
            return json.EnumerateArray().Select(x => ReadScalarText(x)).ToList();

        if (value is System.Collections.IEnumerable sequence && value is not string)
        {
            var result = new List<string?>();
            foreach (var item in sequence) result.Add(ReadScalarText(item));
            return result;
        }

        return value is null ? [] : [ReadScalarText(value)];
    }

    private static string? ReadScalarText(object? value)
    {
        if (value is null) return null;
        if (value is JsonElement json)
        {
            return json.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                JsonValueKind.String => json.GetString(),
                JsonValueKind.Number => json.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => json.GetRawText()
            };
        }

        return Convert.ToString(value, CultureInfo.InvariantCulture);
    }
}
