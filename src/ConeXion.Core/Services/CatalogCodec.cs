using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ConeXion.Core.Models;

namespace ConeXion.Core.Services;

public static class CatalogCodec
{
    private static readonly HashSet<string> CatalogProperties = new(StringComparer.Ordinal)
    { "schema_version", "catalog_version", "generated_at", "productos", "excluidos" };

    private static readonly HashSet<string> ProductProperties = new(StringComparer.Ordinal)
    { "c_interno", "producto", "c_barras", "precio" };

    private static readonly HashSet<string> ExcludedProperties = new(StringComparer.Ordinal)
    { "c_interno", "producto" };

    private sealed class Envelope
    {
        public string Format { get; set; } = "";
        public int FormatVersion { get; set; }
        public string ContentSha256 { get; set; } = "";
        public string Payload { get; set; } = "";
    }

    public static CatalogDocument Decode(ReadOnlySpan<byte> package)
    {
        var bytes = package.ToArray();

        // Wrapper físico histórico soportado: envelope JSON con Base64(GZIP(JSON)).
        try
        {
            var env = JsonSerializer.Deserialize<Envelope>(bytes);
            if (env is not null && !string.IsNullOrWhiteSpace(env.Payload))
            {
                var compressed = Convert.FromBase64String(env.Payload);
                var json = Gunzip(compressed);
                if (!string.IsNullOrWhiteSpace(env.ContentSha256) &&
                    !string.Equals(HashService.Sha256Bytes(json), env.ContentSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("La integridad interna del catálogo no coincide.");
                return ParseCatalogV2(json);
            }
        }
        catch (JsonException) { }

        // Wrapper productivo actual: UTF-8(Base64(GZIP(JSON))).
        // Solo se ignoran fallos del wrapper. Si el JSON se decodifica pero viola schema V2,
        // el error lógico debe propagarse y no reinterpretarse como otro formato.
        byte[]? base64Json = null;
        try
        {
            var text = Encoding.UTF8.GetString(bytes).Trim();
            var compressed = Convert.FromBase64String(text);
            base64Json = Gunzip(compressed);
        }
        catch (FormatException) { }
        catch (InvalidDataException) { }
        if (base64Json is not null)
            return ParseCatalogV2(base64Json);

        // Wrapper físico GZIP(JSON).
        if (bytes.Length >= 2 && bytes[0] == 0x1f && bytes[1] == 0x8b)
            return ParseCatalogV2(Gunzip(bytes));

        // JSON directo solo como wrapper físico; el contenido lógico sigue siendo estrictamente schema V2.
        return ParseCatalogV2(bytes);
    }

    private static byte[] Gunzip(byte[] compressed)
    {
        using var input = new MemoryStream(compressed);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    private static CatalogDocument ParseCatalogV2(byte[] json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("El catálogo debe ser un objeto JSON.");

        EnsureOnlyProperties(root, CatalogProperties, "catálogo");

        if (!root.TryGetProperty("productos", out var productos) || productos.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("El catálogo V2 no contiene productos[].");
        if (!root.TryGetProperty("excluidos", out var excluidos) || excluidos.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("El catálogo V2 no contiene excluidos[].");

        foreach (var item in productos.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("productos[] contiene un elemento inválido.");
            EnsureOnlyProperties(item, ProductProperties, "producto");
        }
        foreach (var item in excluidos.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("excluidos[] contiene un elemento inválido.");
            EnsureOnlyProperties(item, ExcludedProperties, "excluido");
        }

        return JsonSerializer.Deserialize<CatalogDocument>(json)
            ?? throw new InvalidDataException("Catálogo JSON V2 inválido.");
    }

    private static void EnsureOnlyProperties(JsonElement element, HashSet<string> allowed, string context)
    {
        foreach (var property in element.EnumerateObject())
            if (!allowed.Contains(property.Name))
                throw new InvalidDataException($"El {context} contiene un campo no permitido por schema V2: {property.Name}.");
    }
}
