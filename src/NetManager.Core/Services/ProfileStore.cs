using System.Text.Json;
using System.Text.Json.Serialization;
using NetManager.Core.Models;

namespace NetManager.Core.Services;

/// <summary>A saved connection: the IPv4 settings (and static routes) that can be re-applied in one click.</summary>
public sealed class ConnectionProfile
{
    public string Name { get; set; } = "";
    /// <summary>Adapter this profile targets, matched by name (e.g. "Ethernet") or, failing that, MAC address.</summary>
    public string AdapterName { get; set; } = "";
    public string? AdapterMac { get; set; }
    public Ipv4Config Ipv4 { get; set; } = new();
    public List<ProfileRoute> Routes { get; set; } = [];

    [JsonIgnore]
    public string Summary
    {
        get
        {
            var parts = new List<string> { Ipv4.Method == Ipv4Method.Automatic ? "DHCP" : "Manual" };
            if (Ipv4.Method == Ipv4Method.Manual && Ipv4.Addresses.Count > 0) parts.Add(string.Join(", ", Ipv4.Addresses));
            if (Ipv4.Method == Ipv4Method.Manual && !string.IsNullOrEmpty(Ipv4.Gateway)) parts.Add($"gw {Ipv4.Gateway}");
            if (!Ipv4.DnsAutomatic && Ipv4.DnsServers.Count > 0) parts.Add($"DNS {string.Join(", ", Ipv4.DnsServers)}");
            if (Routes.Count > 0) parts.Add($"{Routes.Count} route{(Routes.Count == 1 ? "" : "s")}");
            return string.Join(" · ", parts);
        }
    }
}

public sealed class ProfileRoute
{
    public string Destination { get; set; } = "";
    public int PrefixLength { get; set; }
    public string? NextHop { get; set; }
    public int Metric { get; set; } = 1;
    public bool Persistent { get; set; }
}

public sealed class ProfileStore(string? path = null)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string FilePath { get; } = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NetManager", "profiles.json");

    public List<ConnectionProfile> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            return JsonSerializer.Deserialize<List<ConnectionProfile>>(File.ReadAllText(FilePath), Json) ?? [];
        }
        catch (JsonException) { return []; }
    }

    public void Save(IEnumerable<ConnectionProfile> profiles)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(profiles, Json));
        File.Move(tmp, FilePath, overwrite: true);
    }

    public static AdapterInfo? FindAdapter(ConnectionProfile p, IEnumerable<AdapterInfo> adapters)
    {
        var list = adapters.ToList();
        return list.FirstOrDefault(a => a.Name.Equals(p.AdapterName, StringComparison.OrdinalIgnoreCase))
            ?? (p.AdapterMac is null ? null : list.FirstOrDefault(a => string.Equals(a.MacAddress, p.AdapterMac, StringComparison.OrdinalIgnoreCase)));
    }
}
