using System.Diagnostics;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;
using Microsoft.Win32;
using NetManager.Core.Models;
using NetManager.Core.Services;

namespace NetManager.Windows;

/// <summary>
/// Windows backend. Reads state with System.Net.NetworkInformation, the IP Helper API and WMI;
/// writes through WMI (Win32_NetworkAdapterConfiguration) and the IP Helper API. Needs an elevated process to write.
/// </summary>
public sealed class WindowsNetworkService : INetworkService
{
    public string BackendName => "Windows (IP Helper + WMI)";

    public bool CanModify { get; } =
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    // ---------------------------------------------------------------- adapters

    public Task<IReadOnlyList<AdapterInfo>> GetAdaptersAsync(CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<AdapterInfo>>(ReadAdapters, ct);

    private static List<AdapterInfo> ReadAdapters()
    {
        // The IP stack (NetworkInterface) does not list administratively disabled adapters, so WMI is the
        // source of truth for them — exactly what the "Network Connections" window shows.
        var wmi = ReadWmiAdapters();
        var disabledIds = wmi.Where(w => w.Disabled).Select(w => w.Guid).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<AdapterInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback) continue;
            if (!nic.Supports(NetworkInterfaceComponent.IPv4)) continue;

            IPInterfaceProperties props;
            IPv4InterfaceProperties v4;
            try { props = nic.GetIPProperties(); v4 = props.GetIPv4Properties(); }
            catch (NetworkInformationException) { continue; }

            var ipv4 = new Ipv4Config
            {
                Method = v4.IsDhcpEnabled ? Ipv4Method.Automatic : Ipv4Method.Manual,
                Addresses = props.UnicastAddresses
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => new Ipv4AddressEntry(a.Address.ToString(), a.PrefixLength))
                    .ToList(),
                Gateway = props.GatewayAddresses
                    .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))
                    ?.Address.ToString(),
                DnsAutomatic = !HasStaticDns(nic.Id),
                DnsServers = props.DnsAddresses
                    .Where(d => d.AddressFamily == AddressFamily.InterNetwork)
                    .Select(d => d.ToString()).ToList(),
            };

            AdapterStatus status;
            if (disabledIds.Contains(nic.Id)) status = AdapterStatus.Disabled;
            else status = nic.OperationalStatus == OperationalStatus.Up ? AdapterStatus.Connected : AdapterStatus.Disconnected;

            var mac = nic.GetPhysicalAddress().ToString();
            result.Add(new AdapterInfo
            {
                Id = nic.Id,
                InterfaceIndex = v4.Index,
                Name = nic.Name,
                Description = nic.Description,
                Kind = ClassifyKind(nic),
                Status = status,
                MacAddress = mac.Length == 12 ? string.Join(':', Enumerable.Range(0, 6).Select(i => mac.Substring(i * 2, 2))) : null,
                SpeedBitsPerSecond = nic.Speed,
                Mtu = v4.Mtu,
                DhcpServer = props.DhcpServerAddresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)?.ToString(),
                Ipv4 = ipv4,
            });
        }
        // Disabled adapters that the IP stack no longer reports.
        foreach (var w in wmi.Where(w => w.Disabled && !result.Any(a => a.Id.Equals(w.Guid, StringComparison.OrdinalIgnoreCase))))
        {
            result.Add(new AdapterInfo
            {
                Id = w.Guid,
                InterfaceIndex = w.InterfaceIndex,
                Name = w.Name,
                Description = w.Description,
                Kind = ClassifyKind(w.Description),
                Status = AdapterStatus.Disabled,
                MacAddress = w.Mac,
                Ipv4 = new Ipv4Config(),
            });
        }

        return result.OrderBy(a => a.Kind == AdapterKind.Tunnel).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static AdapterKind ClassifyKind(NetworkInterface nic)
    {
        if (nic.NetworkInterfaceType is NetworkInterfaceType.Tunnel) return AdapterKind.Tunnel;
        if (nic.NetworkInterfaceType is NetworkInterfaceType.Wireless80211) return AdapterKind.Wireless;
        var byName = ClassifyKind(nic.Description);
        if (byName == AdapterKind.Virtual) return byName;
        return nic.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet
            or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx
            ? AdapterKind.Ethernet : AdapterKind.Other;
    }

    /// <summary>Guess from the driver description; used for adapters the IP stack doesn't report (disabled ones).</summary>
    private static AdapterKind ClassifyKind(string description)
    {
        string[] virtualMarkers = ["Virtual", "Hyper-V", "VMware", "VirtualBox", "TAP-", "Wintun", "WireGuard", "Npcap", "Loopback Adapter"];
        if (virtualMarkers.Any(m => description.Contains(m, StringComparison.OrdinalIgnoreCase))) return AdapterKind.Virtual;
        string[] wifiMarkers = ["Wi-Fi", "Wireless", "802.11", "WLAN"];
        if (wifiMarkers.Any(m => description.Contains(m, StringComparison.OrdinalIgnoreCase))) return AdapterKind.Wireless;
        return AdapterKind.Ethernet;
    }

    /// <summary>A manually configured DNS list is stored as a non-empty NameServer value for the interface GUID.</summary>
    private static bool HasStaticDns(string interfaceGuid)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{interfaceGuid}");
        return !string.IsNullOrWhiteSpace(key?.GetValue("NameServer") as string);
    }

    private sealed record WmiAdapter(string Guid, int InterfaceIndex, string Name, string Description, string? Mac, bool Disabled);

    /// <summary>Win32_NetworkAdapter rows that have a connection name (what ncpa.cpl lists). ConfigManagerErrorCode 22 = device disabled.</summary>
    private static List<WmiAdapter> ReadWmiAdapters()
    {
        var list = new List<WmiAdapter>();
        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT GUID, InterfaceIndex, NetConnectionID, Name, MACAddress, ConfigManagerErrorCode FROM Win32_NetworkAdapter WHERE NetConnectionID IS NOT NULL");
            foreach (ManagementObject o in s.Get())
            {
                using (o)
                {
                    if (o["GUID"] is not string guid) continue;
                    list.Add(new WmiAdapter(
                        guid,
                        o["InterfaceIndex"] is null ? 0 : Convert.ToInt32(o["InterfaceIndex"]),
                        o["NetConnectionID"] as string ?? (o["Name"] as string ?? guid),
                        o["Name"] as string ?? "",
                        o["MACAddress"] as string,
                        o["ConfigManagerErrorCode"] is not null && Convert.ToUInt32(o["ConfigManagerErrorCode"]) == 22));
                }
            }
        }
        catch (ManagementException) { /* without WMI we simply can't list disabled adapters */ }
        return list;
    }

    private static Dictionary<int, int> ReadInterfaceMetrics()
    {
        var map = new Dictionary<int, int>();
        try
        {
            using var s = new ManagementObjectSearcher("SELECT InterfaceIndex, IPConnectionMetric FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = TRUE");
            foreach (ManagementObject o in s.Get())
            {
                using (o)
                    if (o["InterfaceIndex"] is not null && o["IPConnectionMetric"] is not null)
                        map[Convert.ToInt32(o["InterfaceIndex"])] = Convert.ToInt32(o["IPConnectionMetric"]);
            }
        }
        catch (ManagementException) { }
        return map;
    }

    // ---------------------------------------------------------------- configuration

    public Task ApplyIpv4Async(int interfaceIndex, Ipv4Config config, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var problems = Ipv4Util.Validate(config);
            if (problems.Count > 0) throw new NetworkOperationException(string.Join(Environment.NewLine, problems));
            RequireAdmin();

            using var cfg = GetAdapterConfig(interfaceIndex);
            if (config.Method == Ipv4Method.Automatic)
            {
                WmiCheck("EnableDHCP", cfg.InvokeMethod("EnableDHCP", null));
            }
            else
            {
                string[] ips = config.Addresses.Select(a => a.Address).ToArray();
                string[] masks = config.Addresses.Select(a => a.Mask).ToArray();
                // EnableStatic replaces every IPv4 address on the adapter and drops any DHCP lease.
                WmiCheck("EnableStatic", cfg.InvokeMethod("EnableStatic", [ips, masks]));

                if (!string.IsNullOrWhiteSpace(config.Gateway))
                    SetGateway(cfg, config.Gateway!.Trim());
                else
                    DeleteDefaultRoutes(interfaceIndex);
            }

            // DNS is independent of the address method: DHCP addresses can still use hand-written servers.
            object? dns = config.DnsAutomatic || config.DnsServers.Count == 0
                ? null
                : config.DnsServers.Select(s => s.Trim()).ToArray();
            WmiCheck("SetDNSServerSearchOrder", cfg.InvokeMethod("SetDNSServerSearchOrder", [dns]));
        }, ct);

    private static void SetGateway(ManagementObject cfg, string gateway)
    {
        // Metric 0 asks for an automatic metric; some builds reject it, so retry with a fixed cost of 1.
        uint rc = (uint)cfg.InvokeMethod("SetGateways", [new[] { gateway }, new ushort[] { 0 }]);
        if (rc == 68) rc = (uint)cfg.InvokeMethod("SetGateways", [new[] { gateway }, new ushort[] { 1 }]);
        WmiCheck("SetGateways", rc);
    }

    private static void DeleteDefaultRoutes(int ifIndex)
    {
        foreach (var r in IpForwardTable.Read().Where(r => r.InterfaceIndex == ifIndex && r.PrefixLength == 0 && r.Destination == "0.0.0.0" && r.NextHop is not null))
            IpForwardTable.Delete(r.InterfaceIndex, r.Destination, r.PrefixLength, r.NextHop);
    }

    public Task SetAdapterEnabledAsync(string adapterId, bool enabled, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            RequireAdmin();
            // The id goes into a WQL string, so only ever accept a real GUID.
            if (!Guid.TryParse(adapterId, out var guid)) throw new NetworkOperationException("Invalid adapter id.");
            var wql = $"SELECT * FROM Win32_NetworkAdapter WHERE GUID = '{guid.ToString("B").ToUpperInvariant()}'";
            using var s = new ManagementObjectSearcher(wql);
            foreach (ManagementObject o in s.Get())
                using (o)
                {
                    var method = enabled ? "Enable" : "Disable";
                    WmiCheck(method, o.InvokeMethod(method, null));
                    return;
                }
            throw new NetworkOperationException("Adapter was not found (it may have been removed).");
        }, ct);

    public Task RenewDhcpLeaseAsync(int interfaceIndex, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            RequireAdmin();
            using var cfg = GetAdapterConfig(interfaceIndex);
            WmiCheck("RenewDHCPLease", cfg.InvokeMethod("RenewDHCPLease", null));
        }, ct);

    public Task FlushDnsCacheAsync(CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var psi = new ProcessStartInfo("ipconfig.exe", "/flushdns") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
            using var p = Process.Start(psi) ?? throw new NetworkOperationException("Could not start ipconfig.");
            p.WaitForExit();
            if (p.ExitCode != 0) throw new NetworkOperationException("ipconfig /flushdns failed (administrator rights are required).");
        }, ct);

    private static ManagementObject GetAdapterConfig(int ifIndex)
    {
        using var s = new ManagementObjectSearcher($"SELECT * FROM Win32_NetworkAdapterConfiguration WHERE InterfaceIndex = {ifIndex}");
        foreach (ManagementObject o in s.Get()) return o;
        throw new NetworkOperationException($"Adapter with interface index {ifIndex} is not IP-enabled or no longer exists.");
    }

    private void RequireAdmin()
    {
        if (!CanModify) throw new NetworkOperationException("Administrator rights are required. Restart NetManager as administrator.");
    }

    private static void WmiCheck(string method, object? returnValue)
    {
        uint code = returnValue is null ? 0 : Convert.ToUInt32(returnValue);
        if (code is 0 or 1) return; // 1 = success, reboot required
        throw new NetworkOperationException($"{method} failed: {DescribeWmiError(code)} (code {code}).");
    }

    private static string DescribeWmiError(uint code) => code switch
    {
        64 => "method not supported on this platform",
        65 => "unknown failure",
        66 => "invalid subnet mask",
        67 => "error processing an instance that was returned",
        68 => "invalid input parameter",
        69 => "more than five gateways specified",
        70 => "invalid IP address",
        71 => "invalid gateway IP address",
        72 => "error accessing the registry",
        73 => "invalid domain name",
        74 => "invalid host name",
        75 => "no primary or secondary WINS server defined",
        76 => "invalid file",
        77 => "invalid system path",
        78 => "file copy failed",
        79 => "invalid security parameter",
        80 => "unable to configure TCP/IP service",
        81 => "unable to configure DHCP service",
        82 => "unable to renew DHCP lease",
        83 => "unable to release DHCP lease",
        84 => "IP not enabled on adapter",
        85 => "IPX not enabled on adapter",
        86 => "frame/network number bounds error",
        87 => "invalid frame type",
        88 => "invalid network number",
        89 => "duplicate network number",
        90 => "parameter out of bounds",
        91 => "access denied",
        92 => "out of memory",
        93 => "already exists",
        94 => "path, file or object not found",
        95 => "unable to notify service",
        96 => "unable to notify DNS service",
        97 => "interface not configurable",
        98 => "not all DHCP leases could be released/renewed",
        100 => "DHCP not enabled on adapter",
        _ => "unspecified error",
    };

    // ---------------------------------------------------------------- routes

    public Task<IReadOnlyList<RouteEntry>> GetRoutesAsync(CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<RouteEntry>>(() =>
        {
            var names = NetworkInterface.GetAllNetworkInterfaces()
                .Select(n => (Index: SafeIndex(n), n.Name)).Where(x => x.Index > 0)
                .GroupBy(x => x.Index).ToDictionary(g => g.Key, g => g.First().Name);
            var metrics = ReadInterfaceMetrics();
            var persistent = PersistentRoutes.LoadKeys();

            return IpForwardTable.Read().Select(r => new RouteEntry
            {
                Destination = r.Destination,
                PrefixLength = r.PrefixLength,
                NextHop = r.NextHop,
                InterfaceIndex = r.InterfaceIndex,
                InterfaceName = names.GetValueOrDefault(r.InterfaceIndex, $"#{r.InterfaceIndex}"),
                Metric = r.Metric,
                InterfaceMetric = metrics.GetValueOrDefault(r.InterfaceIndex),
                Kind = Classify(r),
                Protocol = ProtocolName(r.Protocol, r.Origin),
                IsPersistent = r.NextHop is not null &&
                    persistent.Contains(PersistentRoutes.Key(r.Destination, Ipv4Util.PrefixToMask(r.PrefixLength), r.NextHop)),
            })
            .OrderBy(r => r.Destination == "0.0.0.0" ? 0 : 1)
            .ThenBy(r => Ipv4Util.TryParseAddress(r.Destination, out var ip) ? Ipv4Util.ToUInt(ip) : 0)
            .ThenBy(r => r.PrefixLength)
            .ThenBy(r => r.EffectiveMetric)
            .ToList();
        }, ct);

    private static int SafeIndex(NetworkInterface n)
    {
        try { return n.GetIPProperties().GetIPv4Properties().Index; }
        catch (NetworkInformationException) { return -1; }
    }

    // NL_ROUTE_PROTOCOL: 1 other, 2 local (stack), 3 netmgmt (static / DHCP gateway), 4 icmp redirect, 8 rip, 13 ospf, 14 bgp, 19 dhcp ...
    // NL_ROUTE_ORIGIN:   0 manual, 1 well-known, 2 DHCP, 3 router advertisement, 4 6to4
    private static RouteKind Classify(IpForwardTable.Row r)
    {
        if (r.Protocol == 2 || r.Origin == 1 || r.Loopback) return RouteKind.System;
        if (r.Protocol == 3 && r.Origin == 0) return RouteKind.Static;
        return RouteKind.Dynamic;
    }

    private static string ProtocolName(int protocol, int origin) => protocol switch
    {
        2 => "Local",
        3 when origin == 2 => "DHCP",
        3 => "Static",
        4 => "ICMP redirect",
        8 => "RIP",
        13 => "OSPF",
        14 => "BGP",
        19 => "DHCP",
        _ => $"Other ({protocol})",
    };

    public Task AddRouteAsync(RouteRequest route, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            RequireAdmin();
            if (!Ipv4Util.IsValidAddress(route.Destination)) throw new NetworkOperationException("Destination is not a valid IPv4 address.");
            if (route.PrefixLength is < 0 or > 32) throw new NetworkOperationException("Prefix length must be between 0 and 32.");
            if (route.NextHop is not null && !Ipv4Util.IsValidAddress(route.NextHop)) throw new NetworkOperationException("Gateway is not a valid IPv4 address.");
            if (route.Persistent && route.NextHop is null) throw new NetworkOperationException("A persistent route needs a gateway.");

            int ifIndex = route.InterfaceIndex ?? ResolveInterface(route.NextHop)
                ?? throw new NetworkOperationException("Gateway is not reachable from any adapter; choose an interface explicitly.");

            IpForwardTable.Create(ifIndex, route.Destination, route.PrefixLength, route.NextHop, route.Metric);
            if (route.Persistent) PersistentRoutes.Add(route.Destination, route.PrefixLength, route.NextHop!, route.Metric);
        }, ct);

    public Task DeleteRouteAsync(RouteEntry route, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            RequireAdmin();
            IpForwardTable.Delete(route.InterfaceIndex, route.Destination, route.PrefixLength, route.NextHop);
            PersistentRoutes.Remove(route.Destination, route.PrefixLength, route.NextHop);
        }, ct);

    /// <summary>Finds the adapter that has an address in the same subnet as the gateway.</summary>
    private static int? ResolveInterface(string? nextHop)
    {
        if (nextHop is null) return null;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            IPInterfaceProperties props;
            try { props = nic.GetIPProperties(); } catch (NetworkInformationException) { continue; }
            foreach (var a in props.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
                if (Ipv4Util.InSameSubnet(a.Address.ToString(), nextHop, a.PrefixLength))
                    return SafeIndex(nic);
        }
        return null;
    }
}
