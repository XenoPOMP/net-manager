# NetManager

A NetworkManager-style network settings app for Windows 10/11, written in C# / .NET 10 with [Avalonia](https://avaloniaui.net).

| Feature | Status |
|---|---|
| List adapters (Ethernet, Wi-Fi, virtual…), state, MAC, speed, MTU, DHCP server | ✅ |
| IPv4: DHCP or manual — multiple addresses, netmask/prefix, up to 5 default gateways each with its own (or automatic) metric, interface metric | ✅ |
| DNS: automatic or manual server list (independent of the address method) | ✅ |
| Enable / disable adapter, renew DHCP lease, flush DNS cache | ✅ |
| Routing table: all IPv4 routes, classified Static / Dynamic / System, filter + search | ✅ |
| Add / delete static routes, optionally persistent (`route -p`) | ✅ |
| Profiles: save adapter settings + its static routes, re-activate in one click | ✅ |
| UI in English and Russian (follows the Windows UI language) and a light/dark/system theme — both on the Settings page, applied live and saved in `%APPDATA%\NetManager\settings.json` | ✅ |
| Delete virtual adapters (VirtualBox/VMware host-only) with a confirmation step; hardware, Hyper-V/WSL and system adapters are protected | ✅ |
| Restore: remembers hardware adapters, warns when one disappears, rescans hardware; recreates removed VirtualBox adapters with their IPv4 settings | ✅ |
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

## Removing virtual adapters — safety model

`RemovalPolicy` (Core, unit-tested) is an **allow-list**: an adapter is removable only if its driver service is a known virtual one
(`VBoxNetAdp`, `VBoxNetAdp6`, `VMnetAdapter`). Anything on a hardware bus (`PCI\`, `USB\`, `BTH\`…) or flagged `PhysicalAdapter`
is protected first, then Hyper-V/WSL switches, Microsoft system adapters, and anything unrecognised. The Windows backend
**re-reads the device facts and re-evaluates the policy right before removing** (`pnputil /remove-device`, Windows 10 2004+),
so a UI bug cannot delete a hardware adapter. Before each removal the adapter's details and IPv4 settings go to
`%APPDATA%\NetManager\recovery.json`; the same file remembers every hardware adapter seen. The Maintenance page offers
*Rescan hardware* (`pnputil /scan-devices`) for hardware that was uninstalled by mistake, and *Recreate* for VirtualBox adapters
(`VBoxManage hostonlyif create`, then the saved IPv4 settings are re-applied).

## Translations

All user-visible text lives in `src/NetManager.Core/Localization/EnglishStrings.cs` and `RussianStrings.cs` (key → text, `{0}` placeholders).
XAML uses `{loc:T some.key}`, code uses `Loc.T("some.key", args)`. To add a language: add a table, register it in `Loc.cs`, and add it to
`MainViewModel.Languages`. A unit test fails if a key is missing from a language or its placeholders differ.

## How it talks to Windows

- **Read adapters** — `System.Net.NetworkInformation`, plus WMI (`Win32_NetworkAdapter` for enabled state) and the
  registry (`Tcpip\Parameters\Interfaces\{GUID}\NameServer` to tell static DNS from DHCP DNS).
- **Write IPv4 / DNS** — WMI `Win32_NetworkAdapterConfiguration` (`EnableStatic`, `EnableDHCP`, `SetGateways`,
  `SetDNSServerSearchOrder`, `RenewDHCPLease`).
- **Routes** — IP Helper `GetIpForwardTable2` / `CreateIpForwardEntry2` / `DeleteIpForwardEntry2`. Persistent routes are
  written to `HKLM\...\Tcpip\Parameters\PersistentRoutes`, the same place `route -p add` uses.
- The exe manifest requests **administrator** rights (UAC prompt on start). If the app is started without them (e.g. `dotnet run`) it relaunches itself elevated once; if UAC is declined it stays read-only with a banner and a **Restart as administrator** button. `--no-elevate` disables the relaunch for debugging.

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
6. Maintenance page: with VirtualBox installed, delete one *host-only* adapter → it disappears from Windows' Network Connections;
   *Recreate* brings back a host-only adapter with its old IPv4 settings. Check that no hardware / Hyper-V adapter is ever listed as removable.
7. Add two gateways on a spare adapter (one with *Auto metric*, one with metric 20) and an interface metric; compare with `route print -4`
   (effective metric = gateway metric + interface metric) and with Windows' own Advanced TCP/IP dialog. Remove all gateways and reboot-check that none return.
   If setting gateways returns WMI error 68, report it — the code retries automatic ones with a fixed metric of 1.
