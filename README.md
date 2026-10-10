# NetManager

A NetworkManager-style network settings app for Windows 10/11, written in C# / .NET 10 with [Avalonia](https://avaloniaui.net).

| Feature | Status |
|---|---|
| List adapters (Ethernet, Wi-Fi, virtual…), state, MAC, speed, MTU, DHCP server | ✅ |
| IPv4: DHCP or manual — multiple addresses, netmask/prefix, gateway | ✅ |
| DNS: automatic or manual server list (independent of the address method) | ✅ |
| Enable / disable adapter, renew DHCP lease, flush DNS cache | ✅ |
| Routing table: all IPv4 routes, classified Static / Dynamic / System, filter + search | ✅ |
| Add / delete static routes, optionally persistent (`route -p`) | ✅ |
| Profiles: save adapter settings + its static routes, re-activate in one click | ✅ |
| IPv6, Wi-Fi scan/connect, VPN, proxy, MTU/MAC override, interface metric, DNS search suffixes, bridge/VLAN | ⏳ planned |

## Layout

```
src/NetManager.Core      models, IPv4 validation, INetworkService, profile store, MockNetworkService
src/NetManager.Windows   WindowsNetworkService: IP Helper API (iphlpapi) + WMI + registry
src/NetManager.App       Avalonia UI (MVVM, CommunityToolkit.Mvvm)
tests/NetManager.Core.Tests
```

The UI only talks to `INetworkService`. On Windows the app uses `WindowsNetworkService`; on macOS/Linux it falls back to
`MockNetworkService` (demo data), so the whole UI can be developed and run without a Windows machine.

## How it talks to Windows

- **Read adapters** — `System.Net.NetworkInformation`, plus WMI (`Win32_NetworkAdapter` for enabled state) and the
  registry (`Tcpip\Parameters\Interfaces\{GUID}\NameServer` to tell static DNS from DHCP DNS).
- **Write IPv4 / DNS** — WMI `Win32_NetworkAdapterConfiguration` (`EnableStatic`, `EnableDHCP`, `SetGateways`,
  `SetDNSServerSearchOrder`, `RenewDHCPLease`).
- **Routes** — IP Helper `GetIpForwardTable2` / `CreateIpForwardEntry2` / `DeleteIpForwardEntry2`. Persistent routes are
  written to `HKLM\...\Tcpip\Parameters\PersistentRoutes`, the same place `route -p add` uses.
- The exe manifest requests **administrator** rights (UAC prompt on start); without them the UI shows a warning banner.

## Develop

```bash
dotnet run --project src/NetManager.App      # runs with demo data on macOS/Linux, real backend on Windows
dotnet test                                  # unit tests (IPv4 maths, validation, profile store)
./publish-win.sh [win-x64|win-x86|win-arm64] # cross-build a self-contained NetManager.exe into publish/<rid>/
```

## Windows smoke-test checklist

The Windows backend has been compile-checked but could only be written, not executed, on macOS.
On first run on a Windows 10 machine (as admin), check in this order — ideally on a VM, since mistakes can cut your network:

1. Connections lists your adapters with correct IP / MAC / state.
2. Routing table matches `route print -4` (Metric column = route metric + interface metric).
3. Add a harmless static route (e.g. `10.99.0.0/24` via your gateway) → appears in `route print`; delete it → gone.
4. Tick *Persistent*, add a route, check `HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\PersistentRoutes`.
5. On a spare/virtual adapter: switch Manual ⇄ Automatic, change DNS, check with `ipconfig /all`.
6. If setting a default gateway returns WMI error 68, report it — the code retries with a fixed metric of 1 first.
