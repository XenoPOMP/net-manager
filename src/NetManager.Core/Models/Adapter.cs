namespace NetManager.Core.Models;

public enum AdapterKind { Ethernet, Wireless, Virtual, Tunnel, Loopback, Other }

public enum AdapterStatus { Connected, Disconnected, Disabled }

/// <summary>A snapshot of a network adapter and its current IPv4 state.</summary>
public sealed record AdapterInfo
{
    /// <summary>Stable adapter GUID (e.g. "{1A2B…}"). Unlike the interface index it survives disabling the adapter.</summary>
    public required string Id { get; init; }
    /// <summary>IP interface index; 0 when the adapter is disabled and has none.</summary>
    public required int InterfaceIndex { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public AdapterKind Kind { get; init; }
    public AdapterStatus Status { get; init; }
    public string? MacAddress { get; init; }
    public long SpeedBitsPerSecond { get; init; }
    public int Mtu { get; init; }
    public string? DhcpServer { get; init; }
    public required Ipv4Config Ipv4 { get; init; }

    public bool IsEnabled => Status != AdapterStatus.Disabled;
    public bool IsConnected => Status == AdapterStatus.Connected;
    public bool IsDisabled => Status == AdapterStatus.Disabled;
    public string StatusText => Status switch { AdapterStatus.Connected => "Connected", AdapterStatus.Disconnected => "Cable unplugged / not connected", _ => "Disabled" };
    public string KindText => Kind.ToString();
    /// <summary>Second line of the list row: the address, or the reason there isn't one.</summary>
    public string RowStatus => Status switch
    {
        AdapterStatus.Disabled => "Disabled",
        AdapterStatus.Disconnected when Ipv4.Addresses.Count == 0 => "Not connected",
        _ => PrimaryAddress,
    };
    public string PrimaryAddress => Ipv4.Addresses.FirstOrDefault()?.ToString() ?? "No address";
    public string SpeedText => SpeedBitsPerSecond switch
    {
        <= 0 or long.MaxValue => "—",
        >= 1_000_000_000 => $"{SpeedBitsPerSecond / 1_000_000_000.0:0.#} Gbit/s",
        _ => $"{SpeedBitsPerSecond / 1_000_000.0:0.#} Mbit/s",
    };
    public bool CanConfigure => Kind != AdapterKind.Loopback;
}
