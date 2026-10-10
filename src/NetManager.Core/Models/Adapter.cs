using NetManager.Core.Localization;

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
    /// <summary>Device instance id (e.g. "PCI\VEN_8086…" or "ROOT\NET\0002"); null when unknown.</summary>
    public string? PnpId { get; init; }
    /// <summary>Driver service name (e.g. "VBoxNetAdp6").</summary>
    public string? Service { get; init; }
    /// <summary>Real bus-attached hardware (as opposed to a software adapter).</summary>
    public bool IsHardware { get; init; }
    public RemovalDecision Removal { get; init; } = RemovalDecision.Unknown;
    public required Ipv4Config Ipv4 { get; init; }

    public bool IsEnabled => Status != AdapterStatus.Disabled;
    public bool IsConnected => Status == AdapterStatus.Connected;
    public bool IsDisabled => Status == AdapterStatus.Disabled;
    public string StatusText => Loc.T($"adapter.status.{Status}");
    public string KindText => Loc.T($"adapter.kind.{Kind}");
    /// <summary>Second line of the list row: the address, or the reason there isn't one.</summary>
    public string RowStatus => Status switch
    {
        AdapterStatus.Disabled => Loc.T("adapter.status.Disabled"),
        AdapterStatus.Disconnected when Ipv4.Addresses.Count == 0 => Loc.T("adapter.row.notConnected"),
        _ => PrimaryAddress,
    };
    public string PrimaryAddress => Ipv4.Addresses.FirstOrDefault()?.ToString() ?? Loc.T("adapter.noAddress");
    public string SpeedText => SpeedBitsPerSecond switch
    {
        <= 0 or long.MaxValue => "—",
        >= 1_000_000_000 => Loc.T("speed.gbit", SpeedBitsPerSecond / 1_000_000_000.0),
        _ => Loc.T("speed.mbit", SpeedBitsPerSecond / 1_000_000.0),
    };
    public bool CanRemove => Removal.Allowed;
    /// <summary>One line for the detail panel: why the adapter is protected, or where it can be removed.</summary>
    public string RemovalNote => Removal.Allowed ? Loc.T("removal.ok") : Loc.T("conn.protected", Loc.T(Removal.ReasonKey));
    public bool CanConfigure => Kind != AdapterKind.Loopback;
}
