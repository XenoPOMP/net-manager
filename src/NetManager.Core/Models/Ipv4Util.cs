using System.Net;
using System.Net.Sockets;

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
            if (c.Addresses.Count == 0) errors.Add("At least one IP address is required.");
            foreach (var a in c.Addresses)
            {
                if (!TryParseAddress(a.Address, out var ip)) { errors.Add($"'{a.Address}' is not a valid IPv4 address."); continue; }
                if (a.PrefixLength is < 1 or > 32) errors.Add($"Subnet mask of {a.Address} is invalid.");
                uint v = ToUInt(ip);
                if (v == 0 || v >> 28 >= 0xE) errors.Add($"{a.Address} is not a usable unicast address.");
            }
            if (!string.IsNullOrWhiteSpace(c.Gateway))
            {
                if (!TryParseAddress(c.Gateway, out _)) errors.Add($"Gateway '{c.Gateway}' is not a valid IPv4 address.");
                else if (c.Addresses.Count > 0 && !c.Addresses.Any(a => InSameSubnet(a.Address, c.Gateway!, a.PrefixLength)))
                    errors.Add("Gateway is not inside any configured subnet.");
            }
        }
        if (!c.DnsAutomatic)
            foreach (var d in c.DnsServers)
                if (!TryParseAddress(d, out _)) errors.Add($"DNS server '{d}' is not a valid IPv4 address.");
        return errors;
    }
}
