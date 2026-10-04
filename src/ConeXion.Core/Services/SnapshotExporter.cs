using System.Text.Json;
using ConeXion.Core.Models;

namespace ConeXion.Core.Services;

public static class SnapshotExporter
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };
    public static string SerializeCompact(SnapshotDocument snapshot) => JsonSerializer.Serialize(snapshot, Options);
    public static SnapshotDocument Deserialize(string json) => JsonSerializer.Deserialize<SnapshotDocument>(json, Options)
        ?? throw new InvalidDataException("Snapshot local inválido.");
}
