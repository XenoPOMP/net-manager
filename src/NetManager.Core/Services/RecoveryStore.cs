using System.Text.Json;
using System.Text.Json.Serialization;
using NetManager.Core.Models;

namespace NetManager.Core.Services;

/// <summary>A hardware adapter NetManager has seen on this machine.</summary>
public sealed class KnownAdapter
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string? PnpId { get; set; }
    public string? Mac { get; set; }
}

/// <summary>A virtual adapter removed through NetManager, with what is needed to bring it back.</summary>
public sealed class RemovedAdapterRecord
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string? PnpId { get; set; }
    public string? Service { get; set; }
    public string? Mac { get; set; }
    public DateTime RemovedAtUtc { get; set; } = DateTime.UtcNow;
    public Ipv4Config Ipv4 { get; set; } = new();
}

public sealed class RecoveryData
{
    public List<KnownAdapter> Hardware { get; set; } = [];
    public List<RemovedAdapterRecord> Removed { get; set; } = [];
}

public sealed class RecoveryStore(string? path = null)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public string FilePath { get; } = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NetManager", "recovery.json");

    public RecoveryData Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<RecoveryData>(File.ReadAllText(FilePath), Json) ?? new() : new(); }
        catch (JsonException) { return new(); }
    }

    public void Save(RecoveryData data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(data, Json));
        File.Move(tmp, FilePath, overwrite: true);
    }
}

/// <summary>Pure functions behind "this hardware adapter used to be here".</summary>
public static class RecoveryLogic
{
    private static bool Same(KnownAdapter k, AdapterInfo a) =>
        (!string.IsNullOrEmpty(k.PnpId) && string.Equals(k.PnpId, a.PnpId, StringComparison.OrdinalIgnoreCase))
        || (!string.IsNullOrEmpty(k.Mac) && string.Equals(k.Mac, a.MacAddress, StringComparison.OrdinalIgnoreCase));

    /// <summary>Remembers every hardware adapter currently present. Returns true when the data changed.</summary>
    public static bool UpdateSnapshot(RecoveryData data, IEnumerable<AdapterInfo> adapters)
    {
        bool changed = false;
        foreach (var a in adapters.Where(a => a.IsHardware))
        {
            var known = data.Hardware.FirstOrDefault(k => Same(k, a));
            if (known is null)
            {
                data.Hardware.Add(new KnownAdapter { Name = a.Name, Description = a.Description, PnpId = a.PnpId, Mac = a.MacAddress });
                changed = true;
            }
            else if (known.Name != a.Name || known.Description != a.Description || known.PnpId != a.PnpId || known.Mac != a.MacAddress)
            {
                known.Name = a.Name; known.Description = a.Description; known.PnpId = a.PnpId ?? known.PnpId; known.Mac = a.MacAddress ?? known.Mac;
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>Remembered hardware adapters that Windows no longer lists (uninstalled — or just unplugged).</summary>
    public static List<KnownAdapter> FindMissing(RecoveryData data, IEnumerable<AdapterInfo> adapters)
    {
        var list = adapters.ToList();
        return data.Hardware.Where(k => !list.Any(a => Same(k, a))).ToList();
    }
}
