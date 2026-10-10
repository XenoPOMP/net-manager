using NetManager.Core.Models;
using NetManager.Core.Services;

namespace NetManager.Core.Tests;

public class ProfileMatcherTests
{
    private static Ipv4Config Manual() => new()
    {
        Method = Ipv4Method.Manual,
        Addresses = [new("192.168.0.16", 24)],
        Gateways = [new("192.168.0.1", 1)],
        DnsAutomatic = false,
        DnsServers = ["8.8.8.8", "8.8.4.4"],
    };

    private static AdapterInfo Nic(Ipv4Config cfg, AdapterStatus status = AdapterStatus.Connected) => new()
    {
        Id = "{A}", InterfaceIndex = 12, Name = "Ethernet", MacAddress = "82:CC:A2:B9:55:64", Status = status, Ipv4 = cfg,
    };

    private static ConnectionProfile Profile(Ipv4Config cfg, params ProfileRoute[] routes) =>
        new() { Name = "p", AdapterName = "Ethernet", Ipv4 = cfg, Routes = [.. routes] };

    private static RouteEntry Static(string dest, int prefix, string? hop, int ifIndex = 12, bool persistent = false) => new()
    {
        Destination = dest, PrefixLength = prefix, NextHop = hop, InterfaceIndex = ifIndex, Metric = 5, Kind = RouteKind.Static, IsPersistent = persistent,
    };

    private static bool Active(ConnectionProfile p, AdapterInfo a, params RouteEntry[] routes) => ProfileMatcher.IsActive(p, [a], routes);

    [Fact]
    public void IdenticalSettingsAreActive() => Assert.True(Active(Profile(Manual()), Nic(Manual())));

    [Fact]
    public void ChangingAnythingDeactivatesIt()
    {
        var p = Profile(Manual());

        var otherAddress = Manual(); otherAddress.Addresses = [new("192.168.0.17", 24)];
        Assert.False(Active(p, Nic(otherAddress)));

        var otherPrefix = Manual(); otherPrefix.Addresses = [new("192.168.0.16", 16)];
        Assert.False(Active(p, Nic(otherPrefix)));

        var otherGateway = Manual(); otherGateway.Gateways = [new("192.168.0.2", 1)];
        Assert.False(Active(p, Nic(otherGateway)));

        var extraGateway = Manual(); extraGateway.Gateways.Add(new("192.168.0.3"));
        Assert.False(Active(p, Nic(extraGateway)));

        var otherMetric = Manual(); otherMetric.Gateways = [new("192.168.0.1", 20)];
        Assert.False(Active(p, Nic(otherMetric)));

        var otherDns = Manual(); otherDns.DnsServers = ["8.8.4.4", "8.8.8.8"];   // same servers, different order
        Assert.False(Active(p, Nic(otherDns)));

        var autoDns = Manual(); autoDns.DnsAutomatic = true;
        Assert.False(Active(p, Nic(autoDns)));

        var ifMetric = Manual(); ifMetric.InterfaceMetric = 10;
        Assert.False(Active(p, Nic(ifMetric)));

        var dhcp = Manual(); dhcp.Method = Ipv4Method.Automatic;
        Assert.False(Active(p, Nic(dhcp)));
    }

    [Fact]
    public void AddressOrderDoesNotMatter()
    {
        var two = Manual(); two.Addresses = [new("10.0.0.1", 8), new("192.168.0.16", 24)];
        var swapped = Manual(); swapped.Addresses = [new("192.168.0.16", 24), new("10.0.0.1", 8)];
        Assert.True(Active(Profile(two), Nic(swapped)));
    }

    [Fact]
    public void AutomaticGatewayMetricAcceptsWhateverWindowsStored()
    {
        var want = Manual(); want.Gateways = [new("192.168.0.1")];          // profile says "automatic"
        var have = Manual(); have.Gateways = [new("192.168.0.1", 1)];       // Windows had to store 1
        Assert.True(Active(Profile(want), Nic(have)));
    }

    [Fact]
    public void DhcpProfileIgnoresTheLeasedAddress()
    {
        var want = new Ipv4Config { Method = Ipv4Method.Automatic, DnsAutomatic = true };
        var have = new Ipv4Config
        {
            Method = Ipv4Method.Automatic, DnsAutomatic = true, Addresses = [new("192.168.0.21", 24)], Gateways = [new("192.168.0.1")],
        };
        Assert.True(Active(Profile(want), Nic(have)));
    }

    [Fact]
    public void DhcpProfileWithManualDnsNeedsThatDns()
    {
        var want = new Ipv4Config { Method = Ipv4Method.Automatic, DnsAutomatic = false, DnsServers = ["8.8.8.8"] };
        var good = new Ipv4Config { Method = Ipv4Method.Automatic, DnsAutomatic = false, DnsServers = ["8.8.8.8"] };
        var bad = new Ipv4Config { Method = Ipv4Method.Automatic, DnsAutomatic = true, DnsServers = ["192.168.0.1"] };
        Assert.True(Active(Profile(want), Nic(good)));
        Assert.False(Active(Profile(want), Nic(bad)));
    }

    [Fact]
    public void StaticRoutesMustMatchExactly()
    {
        var route = new ProfileRoute { Destination = "10.8.0.0", PrefixLength = 24, NextHop = "192.168.0.250" };
        var p = Profile(Manual(), route);
        var nic = Nic(Manual());

        Assert.True(Active(p, nic, Static("10.8.0.0", 24, "192.168.0.250")));
        Assert.False(Active(p, nic));                                                                         // route missing
        Assert.False(Active(p, nic, Static("10.8.0.0", 24, "192.168.0.250"), Static("10.9.0.0", 24, "192.168.0.250")));   // extra route
        Assert.False(Active(p, nic, Static("10.8.0.0", 24, "192.168.0.250", ifIndex: 99)));                  // on another adapter
    }

    [Fact]
    public void DefaultRoutesAreTheGatewaysAndDoNotCount()
    {
        // the default gateway shows up as a static 0.0.0.0/0 route; it must not make a profile without routes look "changed"
        Assert.True(Active(Profile(Manual()), Nic(Manual()), Static("0.0.0.0", 0, "192.168.0.1")));
    }

    [Fact]
    public void DisabledOrMissingAdapterIsNeverActive()
    {
        Assert.False(Active(Profile(Manual()), Nic(Manual(), AdapterStatus.Disabled)));
        Assert.False(ProfileMatcher.IsActive(Profile(Manual()), [], []));
    }

    [Fact]
    public void ActiveFlagNotifiesAndIsNotSaved()
    {
        var p = Profile(Manual());
        var raised = new List<string?>();
        p.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        p.IsActive = true; p.IsActive = true; p.IsActive = false;
        Assert.Equal(2, raised.Count(n => n == "IsActive"));      // true→true is not a change; only real changes notify
        Assert.Equal(2, raised.Count(n => n == "StateText"));     // the derived text is announced along with it

        p.IsActive = true;
        Assert.DoesNotContain("IsActive", System.Text.Json.JsonSerializer.Serialize(p));
    }
}
