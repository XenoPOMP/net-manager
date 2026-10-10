namespace NetManager.Core.Models;

public enum AdapterKind { Ethernet, Wireless, Virtual, Tunnel, Loopback, Other }

public enum AdapterStatus { Connected, Disconnected, Disabled }

/// <summary>A snapshot of a network adapter and its current IPv4 state.</summary>
public sealed record AdapterInfo
{
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
    public string PrimaryAddress => Ipv4.Addresses.FirstOrDefault()?.ToString() ?? "No address";
    public string SpeedText => SpeedBitsPerSecond switch
    {
        <= 0 or long.MaxValue => "—",
        >= 1_000_000_000 => $"{SpeedBitsPerSecond / 1_000_000_000.0:0.#} Gbit/s",
        _ => $"{SpeedBitsPerSecond / 1_000_000.0:0.#} Mbit/s",
    };
    public bool CanConfigure => Kind != AdapterKind.Loopback;
}
