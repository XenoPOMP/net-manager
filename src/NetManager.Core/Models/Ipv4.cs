using System.Text.Json.Serialization;

namespace NetManager.Core.Models;

public enum Ipv4Method
{
    /// <summary>Address, gateway and (optionally) DNS come from DHCP.</summary>
    Automatic,
    /// <summary>Addresses, gateway and DNS are configured by hand.</summary>
    Manual,
}

public sealed record Ipv4AddressEntry(string Address, int PrefixLength)
{
    public string Mask => Ipv4Util.PrefixToMask(PrefixLength);
    public override string ToString() => $"{Address}/{PrefixLength}";
}

/// <summary>A default gateway. <see cref="Metric"/> null = automatic (like the "Automatic metric" box in Windows).</summary>
public sealed record GatewayEntry(string Address, int? Metric = null)
{
    public override string ToString() => Metric is null ? Address : $"{Address} ({Metric})";
}

/// <summary>The editable IPv4 settings of one adapter (the "IPv4" tab of the NetworkManager editor).</summary>
public sealed class Ipv4Config
{
    public Ipv4Method Method { get; set; } = Ipv4Method.Automatic;
    public List<Ipv4AddressEntry> Addresses { get; set; } = [];
    /// <summary>Default gateways in the order they were configured. Windows allows at most <see cref="MaxGateways"/>.</summary>
    public List<GatewayEntry> Gateways { get; set; } = [];
    /// <summary>Interface metric; null = automatic (Windows derives it from the link speed).</summary>
    public int? InterfaceMetric { get; set; }
    public bool DnsAutomatic { get; set; } = true;
    public List<string> DnsServers { get; set; } = [];

    public const int MaxGateways = 5;

    [JsonIgnore]
    public string? PrimaryGateway => Gateways.FirstOrDefault()?.Address;

    /// <summary>Reads files saved before several gateways were supported ("Gateway": "10.0.0.1"); never written back.</summary>
    [JsonPropertyName("Gateway"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyGateway
    {
        get => null;
        set
        {
            if (!string.IsNullOrWhiteSpace(value) && Gateways.All(g => g.Address != value))
                Gateways.Insert(0, new GatewayEntry(value));
        }
    }

    public Ipv4Config Clone() => new()
    {
        Method = Method,
        Addresses = [.. Addresses],
        Gateways = [.. Gateways],
        InterfaceMetric = InterfaceMetric,
        DnsAutomatic = DnsAutomatic,
        DnsServers = [.. DnsServers],
    };
}
