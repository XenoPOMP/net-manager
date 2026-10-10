using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using NetManager.Core.Localization;
using NetManager.Core.Models;

namespace NetManager.Core.Services;

/// <summary>A saved connection: the IPv4 settings (and static routes) that can be re-applied in one click.</summary>
public sealed class ConnectionProfile : INotifyPropertyChanged
{
    public string Name { get; set; } = "";
    /// <summary>Adapter this profile targets, matched by name (e.g. "Ethernet") or, failing that, MAC address.</summary>
    private string _adapterName = "";

    /// <summary>Notifies: it is refreshed when the adapter was renamed in Windows (see ProfilesViewModel.RefreshActiveAsync).</summary>
    public string AdapterName
    {
        get => _adapterName;
        set { if (_adapterName != value) { _adapterName = value; Raise(nameof(AdapterName)); Raise(nameof(AdapterProblemText)); Raise(nameof(StateText)); } }
    }
    public string? AdapterMac { get; set; }
    public Ipv4Config Ipv4 { get; set; } = new();
    public List<ProfileRoute> Routes { get; set; } = [];

    private bool _isActive, _adapterMissing, _adapterDisabled;

    /// <summary>Runtime state (never saved): the adapter this profile is for isn't present right now (e.g. an unplugged USB adapter).</summary>
    [JsonIgnore]
    public bool AdapterMissing
    {
        get => _adapterMissing;
        set { if (_adapterMissing != value) { _adapterMissing = value; Raise(nameof(AdapterMissing)); Raise(nameof(HasAdapterProblem)); Raise(nameof(AdapterProblemText)); Raise(nameof(StateText)); } }
    }

    /// <summary>Runtime state (never saved): the adapter exists but is disabled.</summary>
    [JsonIgnore]
    public bool AdapterDisabled
    {
        get => _adapterDisabled;
        set { if (_adapterDisabled != value) { _adapterDisabled = value; Raise(nameof(AdapterDisabled)); Raise(nameof(HasAdapterProblem)); Raise(nameof(AdapterProblemText)); Raise(nameof(StateText)); } }
    }

    [JsonIgnore]
    public bool HasAdapterProblem => _adapterMissing || _adapterDisabled;

    /// <summary>Why this profile can't be used right now ("" when it can).</summary>
    [JsonIgnore]
    public string AdapterProblemText =>
        _adapterMissing ? Loc.T("profiles.adapterMissing", AdapterName)
        : _adapterDisabled ? Loc.T("profiles.adapterDisabled", AdapterName)
        : "";

    /// <summary>One line for the details panel: active, unusable (and why), or simply different from the adapter's current settings.</summary>
    [JsonIgnore]
    public string StateText =>
        _isActive ? Loc.T("profiles.state.active")
        : HasAdapterProblem ? AdapterProblemText
        : Loc.T("profiles.state.inactive");

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>
    /// Saving "DHCP" for one adapter must not replace another adapter's "DHCP": a profile is identified by its name
    /// *and* the adapter it is for (same MAC, or — when there is none — same adapter name).
    /// </summary>
    public bool IsSameSlot(string name, AdapterInfo adapter) =>
        Name.Equals(name, StringComparison.OrdinalIgnoreCase)
        && (!string.IsNullOrEmpty(AdapterMac) && string.Equals(AdapterMac, adapter.MacAddress, StringComparison.OrdinalIgnoreCase)
            || AdapterName.Equals(adapter.Name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True while the adapter's current settings match this profile (see <see cref="ProfileMatcher"/>). Runtime state, never saved:
    /// as soon as anything is changed by hand it turns false again.
    /// </summary>
    [JsonIgnore]
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;
            _isActive = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive)));
            Raise(nameof(StateText));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Default routes (0.0.0.0/0) are the default gateways, which live in <see cref="Ipv4"/>.Gateways. Older versions also copied them into
    /// <see cref="Routes"/>; re-adding them on activation then collides with the gateway (or the DHCP one) that already exists.
    /// </summary>
    public void DropDefaultRoutes() => Routes.RemoveAll(r => r.IsDefaultRoute);

    [JsonIgnore]
    public string Summary
    {
        get
        {
            var parts = new List<string> { Loc.T(Ipv4.Method == Ipv4Method.Automatic ? "profile.dhcp" : "profile.manual") };
            if (Ipv4.Method == Ipv4Method.Manual && Ipv4.Addresses.Count > 0) parts.Add(string.Join(", ", Ipv4.Addresses));
            if (Ipv4.Method == Ipv4Method.Manual && Ipv4.Gateways.Count > 0) parts.Add(Loc.T("profile.gw", string.Join(", ", Ipv4.Gateways)));
            if (!Ipv4.DnsAutomatic && Ipv4.DnsServers.Count > 0) parts.Add(Loc.T("profile.dns", string.Join(", ", Ipv4.DnsServers)));
            if (Routes.Count > 0) parts.Add(Loc.T("profile.routes", Routes.Count));
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

    [JsonIgnore]
    public bool IsDefaultRoute => PrefixLength == 0 && Destination == "0.0.0.0";
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
            var profiles = JsonSerializer.Deserialize<List<ConnectionProfile>>(File.ReadAllText(FilePath), Json) ?? [];
            foreach (var p in profiles) p.DropDefaultRoutes();   // migrates files written before default routes were excluded
            return profiles;
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

    /// <summary>
    /// The MAC address is the stable identity (people rename adapters — "Ethernet" → "MAIN" — and a new adapter can reuse an old name),
    /// so it wins when exactly one adapter has it; if several share it (cloned MACs) the name decides among them. Without a usable MAC
    /// the name is used, which is also what lets a profile follow a replaced adapter that took over the old name.
    /// </summary>
    public static AdapterInfo? FindAdapter(ConnectionProfile p, IEnumerable<AdapterInfo> adapters)
    {
        var list = adapters.ToList();
        bool SameName(AdapterInfo a) => a.Name.Equals(p.AdapterName, StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(p.AdapterMac))
        {
            var byMac = list.Where(a => string.Equals(a.MacAddress, p.AdapterMac, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byMac.Count == 1) return byMac[0];
            if (byMac.Count > 1) return byMac.FirstOrDefault(SameName);
        }
        return list.FirstOrDefault(SameName);
    }
}
