using System.Net;
using System.Net.Sockets;

using NetManager.Core.Localization;

namespace NetManager.Core.Models;

public static class Ipv4Util
{
    public static bool TryParseAddress(string? text, out IPAddress address)
    {
        address = IPAddress.None;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        // IPAddress.TryParse accepts "1", "1.2" and "0x7f.1"; require four dotted decimal octets.
        var parts = text.Split('.');
        if (parts.Length != 4) return false;
        foreach (var p in parts)
            if (p.Length is 0 or > 3 || !p.All(char.IsAsciiDigit) || int.Parse(p) > 255) return false;
        return IPAddress.TryParse(text, out address!) && address.AddressFamily == AddressFamily.InterNetwork;
    }

    public static bool IsValidAddress(string? text) => TryParseAddress(text, out _);

    public static string PrefixToMask(int prefix)
    {
        if (prefix is < 0 or > 32) throw new ArgumentOutOfRangeException(nameof(prefix));
        uint mask = prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
        return ToString(mask);
    }

    /// <summary>Accepts "24", "/24" or "255.255.255.0".</summary>
    public static bool TryParsePrefixOrMask(string? text, out int prefix)
    {
        prefix = -1;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim().TrimStart('/');
        if (text.Contains('.'))
        {
            if (!TryParseAddress(text, out var ip)) return false;
            uint m = ToUInt(ip);
            int ones = System.Numerics.BitOperations.PopCount(m);
            // A valid mask is a run of ones followed by zeros.
            uint expected = ones == 0 ? 0 : uint.MaxValue << (32 - ones);
            if (m != expected) return false;
            prefix = ones;
            return true;
        }
        return int.TryParse(text, out prefix) && prefix is >= 0 and <= 32;
    }

    public static uint ToUInt(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }

    public static string ToString(uint v) => $"{v >> 24}.{(v >> 16) & 255}.{(v >> 8) & 255}.{v & 255}";

    public static bool InSameSubnet(string a, string b, int prefix)
    {
        if (!TryParseAddress(a, out var x) || !TryParseAddress(b, out var y)) return false;
        uint mask = prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
        return (ToUInt(x) & mask) == (ToUInt(y) & mask);
    }

    /// <summary>Validates a configuration; returns human readable problems (empty when valid).</summary>
    public static List<string> Validate(Ipv4Config c)
    {
        var errors = new List<string>();
        if (c.Method == Ipv4Method.Manual)
        {
            if (c.Addresses.Count == 0) errors.Add(Loc.T("val.needAddress"));
            foreach (var a in c.Addresses)
            {
                if (!TryParseAddress(a.Address, out var ip)) { errors.Add(Loc.T("val.badAddress", a.Address)); continue; }
                if (a.PrefixLength is < 1 or > 32) errors.Add(Loc.T("val.badMask", a.Address));
                uint v = ToUInt(ip);
                if (v == 0 || v >> 28 >= 0xE) errors.Add(Loc.T("val.notUnicast", a.Address));
            }
            if (c.Gateways.Count > Ipv4Config.MaxGateways) errors.Add(Loc.T("val.tooManyGateways", Ipv4Config.MaxGateways));
            foreach (var g in c.Gateways)
            {
                if (!TryParseAddress(g.Address, out _)) { errors.Add(Loc.T("val.badGateway", g.Address)); continue; }
                if (c.Addresses.Count > 0 && !c.Addresses.Any(a => InSameSubnet(a.Address, g.Address, a.PrefixLength)))
                    errors.Add(Loc.T("val.gatewayOutside", g.Address));
                if (g.Metric is < 1 or > 9999) errors.Add(Loc.T("val.badMetric", g.Address));
            }
            var dup = c.Gateways.GroupBy(g => g.Address.Trim()).FirstOrDefault(x => x.Count() > 1);
            if (dup is not null) errors.Add(Loc.T("val.dupGateway", dup.Key));
        }
        // The interface metric applies whatever the address method is.
        if (c.InterfaceMetric is < 1 or > 9999) errors.Add(Loc.T("val.badIfMetric"));
        if (!c.DnsAutomatic)
            foreach (var d in c.DnsServers)
                if (!TryParseAddress(d, out _)) errors.Add(Loc.T("val.badDns", d));
        return errors;
    }
}
