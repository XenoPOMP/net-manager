namespace NetManager.Core.Models;

public enum RouteKind
{
    /// <summary>Created by the stack itself: on-link, loopback, multicast, broadcast.</summary>
    System,
    /// <summary>Created by an administrator (route add / this app).</summary>
    Static,
    /// <summary>Learned from DHCP, RIP, OSPF, ICMP redirects and so on.</summary>
    Dynamic,
}

public sealed record RouteEntry
{
    public required string Destination { get; init; }
    public required int PrefixLength { get; init; }
    /// <summary>Null when the destination is directly attached (on-link).</summary>
    public string? NextHop { get; init; }
    public required int InterfaceIndex { get; init; }
    public string InterfaceName { get; init; } = "";
    /// <summary>Route-level metric; the effective cost shown by <c>route print</c> is this plus <see cref="InterfaceMetric"/>.</summary>
    public required int Metric { get; init; }
    public int InterfaceMetric { get; init; }
    public required RouteKind Kind { get; init; }
    public string Protocol { get; init; } = "";
    public bool IsPersistent { get; init; }

    public int EffectiveMetric => Metric + InterfaceMetric;
    public string Mask => Ipv4Util.PrefixToMask(PrefixLength);
    public string DestinationCidr => $"{Destination}/{PrefixLength}";
    public string NextHopDisplay => NextHop ?? "On-link";
    public bool IsDefault => PrefixLength == 0 && Destination == "0.0.0.0";
    /// <summary>System routes and dynamic ones are managed by Windows; only static routes are deleted from the UI.</summary>
    public bool CanDelete => Kind == RouteKind.Static;
}

public sealed record RouteRequest
{
    public required string Destination { get; init; }
    public required int PrefixLength { get; init; }
    public string? NextHop { get; init; }
    /// <summary>When null the backend picks the interface that can reach <see cref="NextHop"/>.</summary>
    public int? InterfaceIndex { get; init; }
    public int Metric { get; init; } = 1;
    /// <summary>Survive reboots (equivalent to <c>route -p add</c>).</summary>
    public bool Persistent { get; init; }
}
