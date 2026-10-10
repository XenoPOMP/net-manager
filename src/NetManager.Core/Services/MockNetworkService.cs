using NetManager.Core.Localization;
using NetManager.Core.Models;

namespace NetManager.Core.Services;

/// <summary>In-memory backend used on non-Windows hosts so the UI can be developed and demoed anywhere.</summary>
public sealed class MockNetworkService : INetworkService
{
    private readonly object _gate = new();
    private readonly List<AdapterInfo> _adapters;
    private readonly List<RouteEntry> _routes;

    public string BackendName => Loc.T("mock.backend");
    public bool CanModify { get; }

    /// <summary>Lets the demo behave like an un-elevated Windows session (<paramref name="canModify"/> = false): reads work, every change is refused.</summary>

    public MockNetworkService(bool canModify = true)
    {
        CanModify = canModify;
        _adapters =
        [
            new()
            {
                Id = "{MOCK-ETH}", InterfaceIndex = 12, Name = "Ethernet", Description = "Intel(R) Ethernet Connection I219-V",
                Kind = AdapterKind.Ethernet, Status = AdapterStatus.Connected, MacAddress = "3C:EC:EF:12:34:56",
                PnpId = @"PCI\VEN_8086&DEV_15BC\3&11583659&0&FE", Service = "e1dexpress", IsHardware = true,
                Removal = new(false, "removal.hardware"),
                SpeedBitsPerSecond = 1_000_000_000, Mtu = 1500, DhcpServer = "192.168.1.1",
                Ipv4 = new Ipv4Config
                {
                    Method = Ipv4Method.Automatic, Addresses = [new("192.168.1.42", 24)], Gateways = [new("192.168.1.1")],
                    DnsAutomatic = true, DnsServers = ["192.168.1.1"],
                },
            },
            new()
            {
                Id = "{MOCK-WIFI}", InterfaceIndex = 7, Name = "Wi-Fi", Description = "Intel(R) Wi-Fi 6 AX201 160MHz",
                Kind = AdapterKind.Wireless, Status = AdapterStatus.Disconnected, MacAddress = "A4:C3:F0:AA:BB:CC",
                PnpId = @"PCI\VEN_8086&DEV_2723\4&2A1B3C&0&00E2", Service = "Netwtw10", IsHardware = true,
                Removal = new(false, "removal.hardware"),
                SpeedBitsPerSecond = 0, Mtu = 1500,
                Ipv4 = new Ipv4Config { Method = Ipv4Method.Automatic },
            },
            new()
            {
                Id = "{MOCK-WSL}", InterfaceIndex = 21, Name = "vEthernet (WSL)", Description = "Hyper-V Virtual Ethernet Adapter",
                Kind = AdapterKind.Virtual, Status = AdapterStatus.Connected, MacAddress = "00:15:5D:01:02:03",
                PnpId = @"ROOT\VMS_MP\0000", Service = "VMSMP", Removal = new(false, "removal.hyperv"),
                SpeedBitsPerSecond = 10_000_000_000, Mtu = 1500,
                Ipv4 = new Ipv4Config
                {
                    Method = Ipv4Method.Manual, Addresses = [new("172.25.96.1", 20)], DnsAutomatic = true,
                },
            },
            VBox(2, 3, "192.168.56.1"),
            VBox(3, 4, "192.168.57.1"),
            VBox(4, 5, "192.168.58.1"),
        ];

        _routes =
        [
            Route("0.0.0.0", 0, "192.168.1.1", 12, 25, 0, RouteKind.Dynamic, "DHCP"),
            Route("10.8.0.0", 24, "192.168.1.250", 12, 5, 0, RouteKind.Static, "Static", persistent: true),
            Route("127.0.0.0", 8, null, 1, 331, 0, RouteKind.System, "Local"),
            Route("172.25.96.0", 20, null, 21, 271, 0, RouteKind.System, "Local"),
            Route("192.168.1.0", 24, null, 12, 281, 0, RouteKind.System, "Local"),
            Route("192.168.1.42", 32, null, 12, 281, 0, RouteKind.System, "Local"),
            Route("224.0.0.0", 4, null, 12, 331, 0, RouteKind.System, "Local"),
            Route("203.0.113.0", 24, "192.168.1.1", 12, 30, 0, RouteKind.Dynamic, "ICMP redirect"),
        ];
        FixInterfaceNames();
    }

    private static AdapterInfo VBox(int n, int index, string ip) => new()
    {
        Id = $"{{MOCK-VBOX-{n}}}", InterfaceIndex = 30 + index, Name = $"Ethernet {n}",
        Description = $"VirtualBox Host-Only Ethernet Adapter #{n}", Kind = AdapterKind.Virtual, Status = AdapterStatus.Connected,
        MacAddress = $"0A:00:27:00:00:{n:X2}", SpeedBitsPerSecond = 1_000_000_000, Mtu = 1500,
        PnpId = $@"ROOT\NET\{n:D4}", Service = "VBoxNetAdp6", Removal = new(true, "removal.ok"),
        Ipv4 = new Ipv4Config { Method = Ipv4Method.Manual, Addresses = [new(ip, 24)], DnsAutomatic = true },
    };

    private RouteEntry Route(string dest, int prefix, string? hop, int ifIndex, int metric, int ifMetric, RouteKind kind, string proto, bool persistent = false) =>
        new()
        {
            Destination = dest, PrefixLength = prefix, NextHop = hop, InterfaceIndex = ifIndex, Metric = metric,
            InterfaceMetric = ifMetric, Kind = kind, Protocol = proto, IsPersistent = persistent,
        };

    private void FixInterfaceNames()
    {
        for (int i = 0; i < _routes.Count; i++)
            _routes[i] = _routes[i] with { InterfaceName = NameOf(_routes[i].InterfaceIndex) };
    }

    private string NameOf(int ifIndex) =>
        ifIndex == 1 ? "Loopback" : _adapters.FirstOrDefault(a => a.InterfaceIndex == ifIndex)?.Name ?? $"#{ifIndex}";

    private void RequireWrite()
    {
        if (!CanModify) throw new NetworkOperationException(Loc.T("err.admin"));
    }

    private static Task Delay(CancellationToken ct) => Task.Delay(250, ct);

    public async Task<IReadOnlyList<AdapterInfo>> GetAdaptersAsync(CancellationToken ct = default)
    {
        await Delay(ct);
        lock (_gate) return [.. _adapters];
    }

    public async Task ApplyIpv4Async(int interfaceIndex, Ipv4Config config, CancellationToken ct = default)
    {
        RequireWrite();
        await Delay(ct);
        var problems = Ipv4Util.Validate(config);
        if (problems.Count > 0) throw new NetworkOperationException(string.Join(Environment.NewLine, problems));
        lock (_gate)
        {
            int i = _adapters.FindIndex(a => a.InterfaceIndex == interfaceIndex);
            if (i < 0) throw new NetworkOperationException(Loc.T("mock.noAdapter"));
            var c = config.Clone();
            if (c.Method == Ipv4Method.Automatic)
            {
                // Pretend DHCP handed out an address.
                c.Addresses = [new("192.168.1.42", 24)];
                c.Gateways = [new("192.168.1.1")];
                if (c.DnsAutomatic) c.DnsServers = ["192.168.1.1"];
            }
            _adapters[i] = _adapters[i] with { Ipv4 = c };
        }
    }

    public async Task SetAdapterEnabledAsync(string adapterId, bool enabled, CancellationToken ct = default)
    {
        RequireWrite();
        await Delay(ct);
        lock (_gate)
        {
            int i = _adapters.FindIndex(a => a.Id == adapterId);
            if (i < 0) throw new NetworkOperationException(Loc.T("mock.noAdapter"));
            _adapters[i] = _adapters[i] with { Status = enabled ? AdapterStatus.Connected : AdapterStatus.Disabled };
        }
    }

    public async Task RemoveAdapterAsync(string adapterId, CancellationToken ct = default)
    {
        RequireWrite();
        await Delay(ct);
        lock (_gate)
        {
            var a = _adapters.FirstOrDefault(x => x.Id == adapterId) ?? throw new NetworkOperationException(Loc.T("mock.noAdapter"));
            // Same rule the Windows backend enforces: whatever the UI asks, protected adapters stay.
            var decision = RemovalPolicy.Evaluate(new AdapterFacts(a.PnpId, a.Service, null, a.Description, a.IsHardware));
            if (!decision.Allowed) throw new NetworkOperationException(Loc.T("err.removeProtected", a.Name, Loc.T(decision.ReasonKey)));
            _adapters.Remove(a);
        }
    }

    public Task RescanHardwareAsync(CancellationToken ct = default) { RequireWrite(); return Delay(ct); }

    public bool CanCreateVirtualBoxAdapter => true;

    public async Task CreateVirtualBoxAdapterAsync(CancellationToken ct = default)
    {
        RequireWrite();
        await Delay(ct);
        lock (_gate)
        {
            int n = 1;
            while (_adapters.Any(a => a.Description.EndsWith($"#{n}"))) n++;
            _adapters.Add(VBox(n, n + 5, $"192.168.{55 + n}.1") with
            {
                Ipv4 = new Ipv4Config { Method = Ipv4Method.Manual, Addresses = [new("192.168.56.1", 24)], DnsAutomatic = true },
            });
        }
    }

    public Task RenewDhcpLeaseAsync(int interfaceIndex, CancellationToken ct = default) { RequireWrite(); return Delay(ct); }
    public Task FlushDnsCacheAsync(CancellationToken ct = default) { RequireWrite(); return Delay(ct); }

    public async Task<IReadOnlyList<RouteEntry>> GetRoutesAsync(CancellationToken ct = default)
    {
        await Delay(ct);
        lock (_gate) return [.. _routes];
    }

    public async Task AddRouteAsync(RouteRequest route, CancellationToken ct = default)
    {
        RequireWrite();
        await Delay(ct);
        lock (_gate)
        {
            int ifIndex = route.InterfaceIndex ?? _adapters.First(a => a.Status == AdapterStatus.Connected).InterfaceIndex;
            if (_routes.Any(r => r.Destination == route.Destination && r.PrefixLength == route.PrefixLength && r.NextHop == route.NextHop && r.InterfaceIndex == ifIndex))
                throw new NetworkOperationException(Loc.T("mock.exists"), kind: NetworkErrorKind.AlreadyExists);
            _routes.Add(Route(route.Destination, route.PrefixLength, route.NextHop, ifIndex, route.Metric, 0, RouteKind.Static, "Static", route.Persistent)
                with { InterfaceName = NameOf(ifIndex) });
        }
    }

    public async Task DeleteRouteAsync(RouteEntry route, CancellationToken ct = default)
    {
        RequireWrite();
        await Delay(ct);
        lock (_gate) _routes.RemoveAll(r => r.Destination == route.Destination && r.PrefixLength == route.PrefixLength
            && r.NextHop == route.NextHop && r.InterfaceIndex == route.InterfaceIndex);
    }
}
