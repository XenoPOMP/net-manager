using NetManager.Core.Models;
using NetManager.Core.Services;

namespace NetManager.Core.Tests;

public class RemovalPolicyTests
{
    private static AdapterFacts Facts(string? pnp = null, string? svc = null, string? mfr = null, string? desc = null, bool? physical = null) =>
        new(pnp, svc, mfr, desc, physical);

    [Theory]
    [InlineData("VBoxNetAdp6")]
    [InlineData("VBoxNetAdp")]
    [InlineData("vboxnetadp6")]       // service names are case-insensitive
    [InlineData("VMnetAdapter")]
    public void KnownVirtualDriversAreRemovable(string service)
    {
        var d = RemovalPolicy.Evaluate(Facts(@"ROOT\NET\0002", service, "Oracle Corporation", "VirtualBox Host-Only Ethernet Adapter #2", physical: false));
        Assert.True(d.Allowed);
    }

    [Theory]
    [InlineData(@"PCI\VEN_8086&DEV_15BC\3&11583659&0&FE")]
    [InlineData(@"USB\VID_0BDA&PID_8153\000001")]
    [InlineData(@"BTH\MS_BTHPAN\7&1F2E3D&0&2")]
    [InlineData(@"pci\ven_10ec&dev_8168\4&1&0&00E1")]   // case-insensitive
    public void HardwareIsNeverRemovableEvenWithAVirtualBoxServiceName(string pnpId)
    {
        // Worst case: something claims to be VirtualBox but sits on a hardware bus.
        var d = RemovalPolicy.Evaluate(Facts(pnpId, "VBoxNetAdp6", "Oracle", "VirtualBox Host-Only Ethernet Adapter", physical: false));
        Assert.False(d.Allowed);
        Assert.Equal("removal.hardware", d.ReasonKey);
    }

    [Fact]
    public void PhysicalFlagAloneProtectsAnAdapter()
    {
        var d = RemovalPolicy.Evaluate(Facts(@"ROOT\NET\0002", "VBoxNetAdp6", physical: true));
        Assert.False(d.Allowed);
        Assert.Equal("removal.hardware", d.ReasonKey);
    }

    [Fact]
    public void HyperVAndWslSwitchesAreProtected()
    {
        Assert.Equal("removal.hyperv", RemovalPolicy.Evaluate(Facts(@"ROOT\VMS_MP\0000", "VMSMP", "Microsoft", "Hyper-V Virtual Ethernet Adapter", false)).ReasonKey);
        Assert.Equal("removal.hyperv", RemovalPolicy.Evaluate(Facts(null, null, null, "Hyper-V Virtual Ethernet Adapter #2", false)).ReasonKey);
    }

    [Fact]
    public void MicrosoftSystemAdaptersAreProtected()
    {
        var d = RemovalPolicy.Evaluate(Facts(@"ROOT\NET\0001", "tunnel", "Microsoft", "Microsoft ISATAP Adapter", false));
        Assert.False(d.Allowed);
        Assert.Equal("removal.system", d.ReasonKey);
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData(@"ROOT\NET\0009", "SomeVpnDriver", "Acme")]
    [InlineData(@"ROOT\NET\0009", "tap0901", "TAP-Windows Provider")]   // VPN-owned: not on the allow-list
    public void UnrecognisedAdaptersAreProtectedByDefault(string? pnp, string? svc, string? mfr)
    {
        var d = RemovalPolicy.Evaluate(Facts(pnp, svc, mfr, "Some adapter", false));
        Assert.False(d.Allowed);
        Assert.Equal("removal.unknown", d.ReasonKey);
    }

    [Fact]
    public async Task MockBackendRefusesToRemoveHardwareAndHyperV()
    {
        var net = new MockNetworkService();
        var adapters = await net.GetAdaptersAsync();

        foreach (var a in adapters.Where(a => !a.CanRemove))
        {
            await Assert.ThrowsAsync<NetworkOperationException>(() => net.RemoveAdapterAsync(a.Id));
            Assert.Contains(a.Id, (await net.GetAdaptersAsync()).Select(x => x.Id));   // still there
        }
        Assert.Contains(adapters, a => a.IsHardware && !a.CanRemove);   // the demo data does contain hardware

        var vbox = adapters.First(a => a.CanRemove);
        await net.RemoveAdapterAsync(vbox.Id);
        Assert.DoesNotContain(vbox.Id, (await net.GetAdaptersAsync()).Select(x => x.Id));
    }
}

public class RecoveryTests
{
    private static AdapterInfo Nic(string name, string? pnp, string? mac, bool hw = true) => new()
    {
        Id = "{" + name + "}", InterfaceIndex = 1, Name = name, Description = name + " desc", PnpId = pnp, MacAddress = mac,
        IsHardware = hw, Ipv4 = new Ipv4Config(),
    };

    [Fact]
    public void SnapshotRemembersHardwareOnly()
    {
        var data = new RecoveryData();
        Assert.True(RecoveryLogic.UpdateSnapshot(data, [Nic("Ethernet", @"PCI\A", "AA:AA"), Nic("vbox", @"ROOT\NET\1", "BB:BB", hw: false)]));
        Assert.Single(data.Hardware);
        Assert.False(RecoveryLogic.UpdateSnapshot(data, [Nic("Ethernet", @"PCI\A", "AA:AA")]));   // unchanged → no write
    }

    [Fact]
    public void DetectsAdapterThatDisappeared()
    {
        var data = new RecoveryData();
        RecoveryLogic.UpdateSnapshot(data, [Nic("Ethernet", @"PCI\A", "AA:AA"), Nic("Wi-Fi", @"PCI\B", "CC:CC")]);

        var missing = RecoveryLogic.FindMissing(data, [Nic("Ethernet", @"PCI\A", "AA:AA")]);
        Assert.Equal("Wi-Fi", Assert.Single(missing).Name);
    }

    [Fact]
    public void RenamedOrReinstalledAdapterIsStillMatched()
    {
        var data = new RecoveryData();
        RecoveryLogic.UpdateSnapshot(data, [Nic("Ethernet", @"PCI\A", "AA:AA")]);
        // renamed in Windows, and the instance id changed after a driver reinstall — MAC still matches
        Assert.Empty(RecoveryLogic.FindMissing(data, [Nic("LAN", @"PCI\A_NEW", "aa:aa")]));
        // renamed with the same instance id
        Assert.Empty(RecoveryLogic.FindMissing(data, [Nic("LAN", @"pci\a", null)]));
    }

    [Fact]
    public void StoreRoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var store = new RecoveryStore(path);
            Assert.Empty(store.Load().Removed);
            var d = new RecoveryData();
            d.Hardware.Add(new KnownAdapter { Name = "Ethernet", PnpId = @"PCI\A", Mac = "AA:AA" });
            d.Removed.Add(new RemovedAdapterRecord
            {
                Name = "Ethernet 3", Description = "VirtualBox Host-Only Ethernet Adapter #3", Service = "VBoxNetAdp6",
                Ipv4 = new Ipv4Config { Method = Ipv4Method.Manual, Addresses = [new("192.168.56.1", 24)] },
            });
            store.Save(d);

            var back = store.Load();
            Assert.Equal(@"PCI\A", Assert.Single(back.Hardware).PnpId);
            var r = Assert.Single(back.Removed);
            Assert.Equal("192.168.56.1/24", r.Ipv4.Addresses[0].ToString());
            Assert.True(RemovalPolicy.IsVirtualBox(r.Service));
        }
        finally { File.Delete(path); }
    }
}

public class ReadOnlyBackendTests
{
    [Fact]
    public async Task UnelevatedMockCanReadButRefusesEveryChange()
    {
        var net = new MockNetworkService(canModify: false);
        Assert.False(net.CanModify);

        // reading still works
        var adapters = await net.GetAdaptersAsync();
        Assert.NotEmpty(adapters);
        Assert.NotEmpty(await net.GetRoutesAsync());

        var a = adapters.First();
        await Assert.ThrowsAsync<NetworkOperationException>(() => net.ApplyIpv4Async(a.InterfaceIndex, new()));
        await Assert.ThrowsAsync<NetworkOperationException>(() => net.SetAdapterEnabledAsync(a.Id, false));
        await Assert.ThrowsAsync<NetworkOperationException>(() => net.RemoveAdapterAsync(adapters.First(x => x.CanRemove).Id));
        await Assert.ThrowsAsync<NetworkOperationException>(() => net.AddRouteAsync(new RouteRequest { Destination = "10.9.0.0", PrefixLength = 24, NextHop = "192.168.1.1" }));
        await Assert.ThrowsAsync<NetworkOperationException>(() => net.RenewDhcpLeaseAsync(a.InterfaceIndex));
        await Assert.ThrowsAsync<NetworkOperationException>(() => net.FlushDnsCacheAsync());
        await Assert.ThrowsAsync<NetworkOperationException>(() => net.RescanHardwareAsync());
        await Assert.ThrowsAsync<NetworkOperationException>(() => net.CreateVirtualBoxAdapterAsync());

        Assert.Equal(adapters.Count, (await net.GetAdaptersAsync()).Count);   // nothing changed
    }
}
