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

/// <summary>The editable IPv4 settings of one adapter (the "IPv4" tab of the NetworkManager editor).</summary>
public sealed class Ipv4Config
{
    public Ipv4Method Method { get; set; } = Ipv4Method.Automatic;
    public List<Ipv4AddressEntry> Addresses { get; set; } = [];
    public string? Gateway { get; set; }
    public bool DnsAutomatic { get; set; } = true;
    public List<string> DnsServers { get; set; } = [];

    public Ipv4Config Clone() => new()
    {
        Method = Method,
        Addresses = [.. Addresses],
        Gateway = Gateway,
        DnsAutomatic = DnsAutomatic,
        DnsServers = [.. DnsServers],
    };
}
