using NetManager.Core.Models;

namespace NetManager.Core.Tests;

public class Ipv4UtilTests
{
    [Theory]
    [InlineData(0, "0.0.0.0")]
    [InlineData(8, "255.0.0.0")]
    [InlineData(24, "255.255.255.0")]
    [InlineData(25, "255.255.255.128")]
    [InlineData(32, "255.255.255.255")]
    public void PrefixToMask(int prefix, string mask) => Assert.Equal(mask, Ipv4Util.PrefixToMask(prefix));

    [Theory]
    [InlineData("24", 24)]
    [InlineData("/16", 16)]
    [InlineData("255.255.255.0", 24)]
    [InlineData("255.255.254.0", 23)]
    [InlineData("0.0.0.0", 0)]
    public void ParsesPrefixOrMask(string text, int expected)
    {
        Assert.True(Ipv4Util.TryParsePrefixOrMask(text, out var p));
        Assert.Equal(expected, p);
    }

    [Theory]
    [InlineData("255.0.255.0")]   // non-contiguous
    [InlineData("33")]
    [InlineData("abc")]
    [InlineData("")]
    public void RejectsBadMask(string text) => Assert.False(Ipv4Util.TryParsePrefixOrMask(text, out _));

    [Theory]
    [InlineData("192.168.1.1", true)]
    [InlineData("1", false)]            // IPAddress.TryParse would accept this
    [InlineData("1.2.3", false)]
    [InlineData("256.1.1.1", false)]
    [InlineData("1.2.3.4.5", false)]
    [InlineData("::1", false)]
    [InlineData("", false)]
    public void AddressValidation(string text, bool valid) => Assert.Equal(valid, Ipv4Util.IsValidAddress(text));

    [Fact]
    public void SubnetMembership()
    {
        Assert.True(Ipv4Util.InSameSubnet("192.168.1.10", "192.168.1.254", 24));
        Assert.False(Ipv4Util.InSameSubnet("192.168.1.10", "192.168.2.1", 24));
        Assert.True(Ipv4Util.InSameSubnet("10.1.2.3", "10.200.0.1", 8));
    }

    [Fact]
    public void ManualConfigValidation()
    {
        var ok = new Ipv4Config
        {
            Method = Ipv4Method.Manual,
            Addresses = [new("192.168.1.10", 24)],
            Gateways = [new("192.168.1.1")],
            DnsAutomatic = false,
            DnsServers = ["1.1.1.1", "8.8.8.8"],
        };
        Assert.Empty(Ipv4Util.Validate(ok));

        var bad = ok.Clone();
        bad.Gateways = [new("10.0.0.1")];
        bad.DnsServers.Add("nope");
        Assert.Equal(2, Ipv4Util.Validate(bad).Count);

        Assert.NotEmpty(Ipv4Util.Validate(new Ipv4Config { Method = Ipv4Method.Manual }));
    }

    [Fact]
    public void AutomaticConfigNeedsNoAddresses() => Assert.Empty(Ipv4Util.Validate(new Ipv4Config()));
}
