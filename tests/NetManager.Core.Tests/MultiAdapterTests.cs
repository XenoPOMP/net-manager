using NetManager.Core.Models;
using NetManager.Core.Services;

namespace NetManager.Core.Tests;

public class MultiAdapterTests
{
    private static AdapterInfo Nic(string id, string name, string mac, int index, Ipv4Config? cfg = null, AdapterStatus status = AdapterStatus.Connected) => new()
    {
        Id = id, InterfaceIndex = index, Name = name, MacAddress = mac, Status = status, Ipv4 = cfg ?? new Ipv4Config(),
    };

    private static ConnectionProfile Profile(string name, string adapter, string? mac, Ipv4Config? cfg = null) =>
        new() { Name = name, AdapterName = adapter, AdapterMac = mac, Ipv4 = cfg ?? new Ipv4Config() };

    // ---- finding the right adapter ------------------------------------------------------------------------------------------

    [Fact]
    public void ProfileFollowsAnAdapterThatWasRenamed()
    {
        // saved when the adapter was called "Ethernet"; the user renamed it to MAIN
        var adapters = new[] { Nic("{1}", "MAIN", "AA:AA", 1), Nic("{2}", "Wi-Fi", "BB:BB", 2) };
        Assert.Equal("MAIN", ProfileStore.FindAdapter(Profile("p", "Ethernet", "AA:AA"), adapters)!.Name);
    }

    [Fact]
    public void ANewAdapterThatReusesTheOldNameDoesNotStealTheProfile()
    {
        // the old adapter (MAC AA) was renamed "Old"; a docking-station adapter now carries the name "Ethernet"
        var adapters = new[] { Nic("{1}", "Old", "AA:AA", 1), Nic("{2}", "Ethernet", "CC:CC", 2) };
        Assert.Equal("Old", ProfileStore.FindAdapter(Profile("p", "Ethernet", "AA:AA"), adapters)!.Name);
    }

    [Fact]
    public void ProfileWithoutAMacFallsBackToTheName()
    {
        var adapters = new[] { Nic("{1}", "Ethernet", "AA:AA", 1), Nic("{2}", "Wi-Fi", "BB:BB", 2) };
        Assert.Equal("Wi-Fi", ProfileStore.FindAdapter(Profile("p", "wi-fi", null), adapters)!.Name);
    }

    [Fact]
    public void ReplacedAdapterTakingOverTheNameStillGetsTheProfile()
    {
        // the MAC is gone (adapter replaced) but another adapter is now called "Ethernet": convenient, and the only option left
        var adapters = new[] { Nic("{2}", "Ethernet", "CC:CC", 2) };
        Assert.Equal("{2}", ProfileStore.FindAdapter(Profile("p", "Ethernet", "AA:AA"), adapters)!.Id);
    }

    [Fact]
    public void SharedMacIsResolvedByName()
    {
        var adapters = new[] { Nic("{1}", "Ethernet 2", "AA:AA", 1), Nic("{2}", "Ethernet 3", "AA:AA", 2) };
        Assert.Equal("{2}", ProfileStore.FindAdapter(Profile("p", "Ethernet 3", "AA:AA"), adapters)!.Id);
    }

    [Fact]
    public void NoMatchMeansNull() =>
        Assert.Null(ProfileStore.FindAdapter(Profile("p", "USB Ethernet", "DD:DD"), [Nic("{1}", "Ethernet", "AA:AA", 1)]));

    // ---- several adapters at once -------------------------------------------------------------------------------------------

    [Fact]
    public void EachAdapterCanHaveItsOwnActiveProfileAtTheSameTime()
    {
        var dhcp = new Ipv4Config { Method = Ipv4Method.Automatic, DnsAutomatic = true };
        var manual = new Ipv4Config { Method = Ipv4Method.Manual, Addresses = [new("10.0.0.5", 24)], DnsAutomatic = true };
        var adapters = new[] { Nic("{1}", "Ethernet", "AA:AA", 1, manual), Nic("{2}", "Wi-Fi", "BB:BB", 2, dhcp) };

        var ethStatic = Profile("Static", "Ethernet", "AA:AA", manual);
        var ethDhcp = Profile("DHCP", "Ethernet", "AA:AA", dhcp);
        var wifiDhcp = Profile("DHCP", "Wi-Fi", "BB:BB", dhcp);

        Assert.True(ProfileMatcher.IsActive(ethStatic, adapters, []));
        Assert.False(ProfileMatcher.IsActive(ethDhcp, adapters, []));       // same name as the Wi-Fi one, but for another adapter
        Assert.True(ProfileMatcher.IsActive(wifiDhcp, adapters, []));
    }

    [Fact]
    public void RoutesOfAnotherAdapterDoNotBreakAProfile()
    {
        var cfg = new Ipv4Config { Method = Ipv4Method.Automatic, DnsAutomatic = true };
        var adapters = new[] { Nic("{1}", "Ethernet", "AA:AA", 1, cfg), Nic("{2}", "Wi-Fi", "BB:BB", 2, cfg) };
        var routeOnWifi = new RouteEntry { Destination = "10.9.0.0", PrefixLength = 24, NextHop = "10.0.0.1", InterfaceIndex = 2, Metric = 5, Kind = RouteKind.Static };
        Assert.True(ProfileMatcher.IsActive(Profile("p", "Ethernet", "AA:AA", cfg), adapters, [routeOnWifi]));
    }

    [Fact]
    public void ADisabledAdapterIsNeverActive() =>
        Assert.False(ProfileMatcher.IsActive(Profile("p", "Ethernet", "AA:AA"), [Nic("{1}", "Ethernet", "AA:AA", 1, status: AdapterStatus.Disabled)], []));

    // ---- saving never overwrites another adapter's profile ------------------------------------------------------------------

    [Fact]
    public void SameNameForAnotherAdapterIsADifferentProfile()
    {
        var ethernet = Nic("{1}", "Ethernet", "AA:AA", 1);
        var wifi = Nic("{2}", "Wi-Fi", "BB:BB", 2);
        var existing = Profile("DHCP", "Ethernet", "AA:AA");

        Assert.True(existing.IsSameSlot("dhcp", ethernet));      // same name, same adapter → saving replaces it
        Assert.False(existing.IsSameSlot("DHCP", wifi));         // same name, other adapter → must coexist
        Assert.False(existing.IsSameSlot("Static", ethernet));   // other name
    }

    [Fact]
    public void SlotFollowsTheMacEvenAfterARename()
    {
        var renamed = Nic("{1}", "MAIN", "AA:AA", 1);
        Assert.True(Profile("DHCP", "Ethernet", "AA:AA").IsSameSlot("DHCP", renamed));
    }

    [Fact]
    public void AdapterProblemFlagsAreRuntimeOnlyAndNotify()
    {
        var p = Profile("p", "Ethernet", "AA:AA");
        var raised = new List<string?>();
        p.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        p.AdapterMissing = true;
        Assert.True(p.HasAdapterProblem);
        Assert.Contains("HasAdapterProblem", raised);
        var json = System.Text.Json.JsonSerializer.Serialize(p);
        Assert.DoesNotContain("AdapterMissing", json);
        Assert.DoesNotContain("HasAdapterProblem", json);
    }
}
