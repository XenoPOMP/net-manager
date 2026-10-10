using System.Diagnostics;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;
using Microsoft.Win32;
using NetManager.Core.Localization;
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
                Gateways = ReadGateways(nic.Id, v4.IsDhcpEnabled, props),
                InterfaceMetric = ReadInterfaceMetric(nic.Id),
                DnsAutomatic = !HasStaticDns(nic.Id),
                DnsServers = props.DnsAddresses
                    .Where(d => d.AddressFamily == AddressFamily.InterNetwork)
                    .Select(d => d.ToString()).ToList(),
            };

            AdapterStatus status;
            if (disabledIds.Contains(nic.Id)) status = AdapterStatus.Disabled;
            else status = nic.OperationalStatus == OperationalStatus.Up ? AdapterStatus.Connected : AdapterStatus.Disconnected;

            var mac = nic.GetPhysicalAddress().ToString();
            var facts = wmi.FirstOrDefault(w => w.Guid.Equals(nic.Id, StringComparison.OrdinalIgnoreCase))?.Facts;
            result.Add(new AdapterInfo
            {
                PnpId = facts?.PnpId,
                Service = facts?.Service,
                IsHardware = facts is not null && RemovalPolicy.IsHardware(facts),
                // Without WMI facts we know nothing about the device, so it stays protected.
                Removal = facts is null ? RemovalDecision.Unknown : RemovalPolicy.Evaluate(facts),
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
                PnpId = w.PnpId,
                Service = w.Service,
                IsHardware = RemovalPolicy.IsHardware(w.Facts),
                Removal = RemovalPolicy.Evaluate(w.Facts),
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

    private sealed record WmiAdapter(string Guid, int InterfaceIndex, string Name, string Description, string? Mac, bool Disabled,
        string? PnpId, string? Service, string? Manufacturer, bool? Physical)
    {
        public AdapterFacts Facts => new(PnpId, Service, Manufacturer, Description, Physical);
    }

    /// <summary>Win32_NetworkAdapter rows that have a connection name (what ncpa.cpl lists). ConfigManagerErrorCode 22 = device disabled.</summary>
    private static List<WmiAdapter> ReadWmiAdapters()
    {
        var list = new List<WmiAdapter>();
        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT GUID, InterfaceIndex, NetConnectionID, Name, MACAddress, ConfigManagerErrorCode, PNPDeviceID, ServiceName, Manufacturer, PhysicalAdapter FROM Win32_NetworkAdapter WHERE NetConnectionID IS NOT NULL");
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
                        o["ConfigManagerErrorCode"] is not null && Convert.ToUInt32(o["ConfigManagerErrorCode"]) == 22,
                        o["PNPDeviceID"] as string,
                        o["ServiceName"] as string,
                        o["Manufacturer"] as string,
                        o["PhysicalAdapter"] as bool?));
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

                if (config.Gateways.Count > 0) SetGateways(cfg, config.Gateways);
                else ClearGateways(interfaceIndex, cfg);
            }

            SetInterfaceMetric(cfg, config.InterfaceMetric);

            // DNS is independent of the address method: DHCP addresses can still use hand-written servers.
            object? dns = config.DnsAutomatic || config.DnsServers.Count == 0
                ? null
                : config.DnsServers.Select(s => s.Trim()).ToArray();
            WmiCheck("SetDNSServerSearchOrder", cfg.InvokeMethod("SetDNSServerSearchOrder", [dns]));
        }, ct);

    /// <summary>
    /// Replaces all default gateways. WMI only accepts explicit metrics of 1–9999: sending 0 for "automatic" fails with
    /// code 90 ("parameter out of bounds", seen on Windows 11). Automatic is expressed by leaving the metric parameter out,
    /// which only works when no gateway has an explicit metric; in a mixed list the automatic ones are stored as metric 1.
    /// </summary>
    private static void SetGateways(ManagementObject cfg, IReadOnlyList<GatewayEntry> gateways)
    {
        string[] addresses = gateways.Select(g => g.Address.Trim()).ToArray();

        if (gateways.All(g => g.Metric is null))
        {
            using var inParams = cfg.GetMethodParameters("SetGateways");
            inParams["DefaultIPGateway"] = addresses;
            using var result = cfg.InvokeMethod("SetGateways", inParams, null);
            uint rc = Convert.ToUInt32(result["ReturnValue"]);
            if (rc is 0 or 1) return;
            if (rc is not (68 or 90)) { WmiCheck("SetGateways", rc); return; }
            // this build wants a metric after all: fall through and store 1
        }

        ushort[] metrics = gateways.Select(g => (ushort)(g.Metric ?? 1)).ToArray();
        WmiCheck("SetGateways", cfg.InvokeMethod("SetGateways", [addresses, metrics]));
    }

    /// <summary>Removes every default gateway: the live routes and the stored configuration (so they don't return after a reboot).</summary>
    private static void ClearGateways(int ifIndex, ManagementObject cfg)
    {
        foreach (var r in IpForwardTable.Read().Where(r => r.InterfaceIndex == ifIndex && r.PrefixLength == 0 && r.Destination == "0.0.0.0" && r.NextHop is not null))
            IpForwardTable.Delete(r.InterfaceIndex, r.Destination, r.PrefixLength, r.NextHop);

        if (cfg["SettingID"] is string guid)
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{TcpipInterfaces}\{guid}", writable: true);
            key?.SetValue("DefaultGateway", Array.Empty<string>(), RegistryValueKind.MultiString);
            key?.SetValue("DefaultGatewayMetric", Array.Empty<string>(), RegistryValueKind.MultiString);
        }
    }

    /// <summary>
    /// Null = automatic. Only touches the system when the value actually changes, so the common "leave it automatic" case never
    /// calls the method. Like gateway metrics, WMI may refuse 0 for automatic (codes 64/68/90); then the stored value is reset in the registry,
    /// which Windows picks up when the interface is next initialised.
    /// </summary>
    private static void SetInterfaceMetric(ManagementObject cfg, int? metric)
    {
        string? guid = cfg["SettingID"] as string;
        if (guid is not null && ReadInterfaceMetric(guid) == metric) return;

        uint rc = (uint)cfg.InvokeMethod("SetIPConnectionMetric", [(uint)(metric ?? 0)]);
        if (rc is 0 or 1) return;
        if (metric is null && guid is not null && rc is 64 or 68 or 90)
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{TcpipInterfaces}\{guid}", writable: true);
            key?.SetValue("InterfaceMetric", 0, RegistryValueKind.DWord);
            return;
        }
        WmiCheck("SetIPConnectionMetric", rc);
    }

    private const string TcpipInterfaces = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";

    /// <summary>
    /// DHCP: whatever the lease provided (metric automatic). Static: the configured list from the registry, which keeps the
    /// per-gateway metrics (REG_MULTI_SZ DefaultGateway / DefaultGatewayMetric, "0" = automatic) even while the link is down.
    /// </summary>
    private static List<GatewayEntry> ReadGateways(string interfaceGuid, bool dhcp, IPInterfaceProperties props)
    {
        if (!dhcp)
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{TcpipInterfaces}\{interfaceGuid}");
            if (key?.GetValue("DefaultGateway") is string[] gws && gws.Length > 0)
            {
                var metrics = key.GetValue("DefaultGatewayMetric") as string[] ?? [];
                return gws.Where(g => !string.IsNullOrWhiteSpace(g))
                    .Select((g, i) => new GatewayEntry(g.Trim(),
                        i < metrics.Length && int.TryParse(metrics[i], out var m) && m > 0 ? m : null))
                    .ToList();
            }
        }
        return props.GatewayAddresses
            .Where(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))
            .Select(g => new GatewayEntry(g.Address.ToString()))
            .ToList();
    }

    private static int? ReadInterfaceMetric(string interfaceGuid)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"{TcpipInterfaces}\{interfaceGuid}");
        return key?.GetValue("InterfaceMetric") is int m && m > 0 ? m : null;
    }

    public Task SetAdapterEnabledAsync(string adapterId, bool enabled, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            RequireAdmin();
            // The id goes into a WQL string, so only ever accept a real GUID.
            if (!Guid.TryParse(adapterId, out var guid)) throw new NetworkOperationException(Loc.T("err.badAdapterId"));
            var wql = $"SELECT * FROM Win32_NetworkAdapter WHERE GUID = '{guid.ToString("B").ToUpperInvariant()}'";
            using var s = new ManagementObjectSearcher(wql);
            foreach (ManagementObject o in s.Get())
                using (o)
                {
                    var method = enabled ? "Enable" : "Disable";
                    WmiCheck(method, o.InvokeMethod(method, null));
                    return;
                }
            throw new NetworkOperationException(Loc.T("err.adapterGone"));
        }, ct);

    public Task RemoveAdapterAsync(string adapterId, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            RequireAdmin();
            if (!Guid.TryParse(adapterId, out var guid)) throw new NetworkOperationException(Loc.T("err.badAdapterId"));

            // Re-read the facts from the system and decide again: the caller's opinion about the adapter is irrelevant.
            var wmi = ReadWmiAdapters().FirstOrDefault(w => Guid.TryParse(w.Guid, out var g) && g == guid)
                ?? throw new NetworkOperationException(Loc.T("err.adapterGone"));
            var decision = RemovalPolicy.Evaluate(wmi.Facts);
            if (!decision.Allowed)
                throw new NetworkOperationException(Loc.T("err.removeProtected", wmi.Name, Loc.T(decision.ReasonKey)));
            if (string.IsNullOrWhiteSpace(wmi.PnpId)) throw new NetworkOperationException(Loc.T("err.noPnpId"));

            RunPnpUtil("/remove-device", wmi.PnpId);
        }, ct);

    public Task RescanHardwareAsync(CancellationToken ct = default) =>
        Task.Run(() =>
        {
            RequireAdmin();
            RunPnpUtil("/scan-devices");
        }, ct);

    private static void RunPnpUtil(params string[] args)
    {
        // "pnputil /remove-device" exists from Windows 10 version 2004 (build 19041).
        if (args[0] == "/remove-device" && Environment.OSVersion.Version.Build < 19041)
            throw new NetworkOperationException(Loc.T("err.pnputilMissing"));

        var exe = Path.Combine(Environment.SystemDirectory, "pnputil.exe");
        ToolRunner.Result r;
        try { r = ToolRunner.Run(exe, args); }
        catch (System.ComponentModel.Win32Exception) { throw new NetworkOperationException(Loc.T("err.pnputilMissing")); }
        // 3010 = success, a reboot is needed to finish.
        if (r.ExitCode is 0 or 3010) return;
        throw new NetworkOperationException(Loc.T("err.pnputil", "pnputil " + args[0], r.ExitCode, r.Output));
    }

    // ---- VirtualBox (recreate removed host-only adapters)

    private static string? FindVBoxManage()
    {
        var dirs = new[]
        {
            Environment.GetEnvironmentVariable("VBOX_MSI_INSTALL_PATH"),
            Environment.GetEnvironmentVariable("VBOX_INSTALL_PATH"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Oracle", "VirtualBox"),
        };
        return dirs.Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => Path.Combine(d!, "VBoxManage.exe")).FirstOrDefault(File.Exists);
    }

    public bool CanCreateVirtualBoxAdapter => FindVBoxManage() is not null;

    public Task CreateVirtualBoxAdapterAsync(CancellationToken ct = default) =>
        Task.Run(() =>
        {
            RequireAdmin();
            var exe = FindVBoxManage() ?? throw new NetworkOperationException(Loc.T("err.vboxMissing"));
            var r = ToolRunner.Run(exe, ["hostonlyif", "create"]);
            if (r.ExitCode != 0) throw new NetworkOperationException(Loc.T("err.pnputil", "VBoxManage hostonlyif create", r.ExitCode, r.Output));
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
            using var p = Process.Start(psi) ?? throw new NetworkOperationException(Loc.T("err.ipconfigStart"));
            p.WaitForExit();
            if (p.ExitCode != 0) throw new NetworkOperationException(Loc.T("err.flushFail"));
        }, ct);

    private static ManagementObject GetAdapterConfig(int ifIndex)
    {
        using var s = new ManagementObjectSearcher($"SELECT * FROM Win32_NetworkAdapterConfiguration WHERE InterfaceIndex = {ifIndex}");
        foreach (ManagementObject o in s.Get()) return o;
        throw new NetworkOperationException(Loc.T("err.noIpAdapter", ifIndex));
    }

    private void RequireAdmin()
    {
        if (!CanModify) throw new NetworkOperationException(Loc.T("err.admin"));
    }

    private static void WmiCheck(string method, object? returnValue)
    {
        uint code = returnValue is null ? 0 : Convert.ToUInt32(returnValue);
        if (code is 0 or 1) return; // 1 = success, reboot required
        throw new NetworkOperationException(Loc.T("err.wmiFailed", method, DescribeWmiError(code), code));
    }

    /// <summary>The most relevant Win32_NetworkAdapterConfiguration return codes are translated; others read "unspecified error".</summary>
    private static string DescribeWmiError(uint code) => Loc.TryT($"wmi.{code}") ?? Loc.T("wmi.unknown");

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
            if (!Ipv4Util.IsValidAddress(route.Destination)) throw new NetworkOperationException(Loc.T("err.badDest"));
            if (route.PrefixLength is < 0 or > 32) throw new NetworkOperationException(Loc.T("err.badPrefix"));
            if (route.NextHop is not null && !Ipv4Util.IsValidAddress(route.NextHop)) throw new NetworkOperationException(Loc.T("err.badGw"));
            if (route.Persistent && route.NextHop is null) throw new NetworkOperationException(Loc.T("err.persistNeedsGw"));

            int ifIndex = route.InterfaceIndex ?? ResolveInterface(route.NextHop)
                ?? throw new NetworkOperationException(Loc.T("err.noIfaceForGw"));

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
