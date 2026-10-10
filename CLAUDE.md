# CLAUDE.md — NetManager

A NetworkManager-style network settings app for **Windows 10/11** (IPv4, DNS, routing table, adapters, profiles, virtual-adapter cleanup).
C# / .NET 10 / Avalonia 12, MVVM with CommunityToolkit.Mvvm. UI in English and Russian.

## Who / where

- The user develops on **macOS (Apple Silicon) with JetBrains Rider**; the target is **Windows 10+ x64**. Windows code can be compiled and
  cross-published here but **never executed here**. Treat every Windows-only behaviour as unverified until the user has run it on Windows.
- The user writes in English; their OS/IDE is Russian-localized (build output, `dotnet` messages come out in Russian: `Ошибок: 0` = 0 errors).
- Project path `/Volumes/Fast and Furious/work/net-manager`; Rider opens it as `/Users/xenopomp/archive/work/net-manager` (symlink to the same folder — harmless).

## Commands

```bash
dotnet build NetManager.slnx                       # whole solution
dotnet test                                        # xunit, Core only (runs fine on macOS)
dotnet run --project src/NetManager.App            # runs with demo data (MockNetworkService) on macOS
./publish-win.sh [win-x64|win-x86|win-arm64]       # self-contained single-file NetManager.exe -> publish/<rid>/
```

Zed users: the same commands are ready-made tasks in `.zed/tasks.json` (`task: spawn` / `cmd-shift-p`). Zed (like the CLI) does a full MSBuild build, so the Rider
"reduced build" problems below don't apply there. Debugging C# in Zed isn't built in (needs a community `netcoredbg` setup); not configured here.

Always finish a change with `dotnet build NetManager.slnx` (expect 0 errors, 0 warnings) and `dotnet test`.
Because output is Russian, grep for `error|Ошибок|Предупреждений` and `Пройден|Неудач`, or just read the tail.

## Layout

```
NetManager.slnx                  solution (new .slnx format)
Directory.Build.props            net10.0, nullable, implicit usings, ProduceReferenceAssembly=false (see Rider gotcha)
src/NetManager.Core              platform-neutral: models, validation, policies, stores, Loc, INetworkService, MockNetworkService
src/NetManager.Windows           WindowsNetworkService: IP Helper (iphlpapi) + WMI + registry + pnputil/VBoxManage
src/NetManager.App               Avalonia UI (Views/*.axaml, ViewModels/*)
tests/NetManager.Core.Tests      xunit tests for Core (IPv4 maths, validation, stores, localization, removal policy, recovery)
```

`NetManager.Windows` targets plain `net10.0` (not `-windows`) on purpose: P/Invoke + `System.Management` + registry work from a
platform-neutral assembly, guarded by `[assembly: SupportedOSPlatform("windows")]`. That lets `NetManager.App` (also `net10.0`) reference it
and pick the backend at runtime: `OperatingSystem.IsWindows() ? WindowsNetworkService : MockNetworkService` (`App.axaml.cs`).

## Architecture

- **`INetworkService`** (Core) is the only thing the UI knows about the OS: adapters, IPv4 apply, enable/disable, DHCP renew, DNS flush,
  routes, remove adapter, rescan hardware, create VirtualBox adapter. Implementations: `WindowsNetworkService`, `MockNetworkService`.
  Failures are reported by throwing `NetworkOperationException` (message is already localized and user-readable).
- **ViewModels** (`App/ViewModels`): `MainViewModel` (nav, language, banners) owns `ConnectionsViewModel`, `RoutesViewModel`, `ProfilesViewModel`,
  `MaintenanceViewModel`. All long operations go through **`AppShell.RunAsync(description, action, successMessage)`** which drives the status
  bar (busy bar, success/error text) and converts exceptions to messages. Don't write try/catch around services in VMs; use `RunAsync`.
- **Models are display-aware**: `AdapterInfo`, `RouteEntry`, `ConnectionProfile` expose computed display strings (`StatusText`, `RowStatus`,
  `NextHopDisplay`, `ProtocolDisplay`, `Summary`…) that call `Loc.T` at read time.
- **Persistence** (JSON under `%APPDATA%\NetManager\`): `profiles.json` (`ProfileStore`), `settings.json` (`SettingsStore`, language),
  `recovery.json` (`RecoveryStore`: remembered hardware adapters + journal of removed virtual adapters). Writes are atomic (tmp + move).

### Feature notes

- **IPv4**: method Automatic/Manual, multiple addresses, **up to 5 default gateways each with an optional metric** (`GatewayEntry(Address, int? Metric)`,
  null = automatic, like Windows' "Automatic metric"), **interface metric** (`Ipv4Config.InterfaceMetric`, null = automatic), DNS (automatic or list — independent
  of the address method). Written via WMI `Win32_NetworkAdapterConfiguration` (`EnableStatic`, `EnableDHCP`, `SetGateways(string[], ushort[])`,
  `SetIPConnectionMetric`, `SetDNSServerSearchOrder`, `RenewDHCPLease`). Automatic metric = 0 in the registry, but WMI `SetGateways` refuses 0 (code 90): automatic is expressed by omitting the metric parameter, falling back to 1. No gateways ⇒ `ClearGateways`: live default routes deleted via IP Helper **and** registry `DefaultGateway`/`DefaultGatewayMetric`
  emptied so they don't return after reboot. Reading: static config comes from registry `Tcpip\Parameters\Interfaces\{GUID}` (`DefaultGateway`,
  `DefaultGatewayMetric`, `InterfaceMetric` — REG_MULTI_SZ/DWORD, "0"/absent = automatic); DHCP gateways come from `NetworkInterface` (metric automatic).
  Effective route metric = gateway metric + interface metric; lowest wins. Old JSON with a single `"Gateway"` still loads (`Ipv4Config.LegacyGateway` setter, never written).
  Static-vs-DHCP DNS is detected from registry `Tcpip\Parameters\Interfaces\{GUID}\NameServer`.
- **Routes**: read with `GetIpForwardTable2`, created/deleted with `CreateIpForwardEntry2`/`DeleteIpForwardEntry2`. `MIB_IPFORWARD_ROW2` is read
  by **raw byte offsets** (documented in `NativeMethods.cs`, row size 104, table header 8). Classification: protocol 2 / origin 1 / loopback ⇒ System;
  protocol 3 + origin 0 ⇒ Static; everything else Dynamic. Only Static routes can be deleted. "Metric" column = route metric + interface metric
  (matches `route print`). Persistent routes = registry `Tcpip\Parameters\PersistentRoutes` value names `dest,mask,gateway,metric` (what `route -p` writes).
- **Adapters list** merges `NetworkInterface` (live data) with WMI `Win32_NetworkAdapter WHERE NetConnectionID IS NOT NULL`: administratively
  disabled adapters (WMI `ConfigManagerErrorCode == 22`) are *not* reported by the IP stack, so they come from WMI only. Identity is the adapter
  **GUID** (`AdapterInfo.Id`), not the interface index (disabled adapters may have none). Enable/disable = WMI `Enable()/Disable()` looked up by GUID
  (the id is `Guid.TryParse`-validated before entering a WQL string).
- **Profiles**: IPv4 settings + the adapter's static routes; adapter matched by name then MAC.
- **Removing virtual adapters** (Maintenance page): `RemovalPolicy` (Core/Models/Removal.cs) is an **allow-list** — removable only when the driver service is
  `VBoxNetAdp`/`VBoxNetAdp6`/`VMnetAdapter` **and** the adapter isn't hardware. Order of evaluation: hardware (PhysicalAdapter flag or PnP id prefix
  `PCI\ USB\ BTH\ …`) → Hyper-V/WSL → allow-list → Microsoft system → unknown (protected). `WindowsNetworkService.RemoveAdapterAsync` **re-reads facts from
  WMI and re-evaluates the policy itself** — never rely on the UI having filtered. Removal = `pnputil /remove-device <instanceId>` (Win10 2004 / build 19041+).
  A journal record (details + IPv4) is written *before* deleting and dropped if deletion fails.
- **Restore**: `RecoveryLogic` remembers hardware adapters (matched by PnP id or MAC), `FindMissing` drives the orange banner; "Rescan hardware" =
  `pnputil /scan-devices`; "Recreate" = `VBoxManage hostonlyif create` then re-apply the journalled IPv4 config to the adapter that appeared.
  `ToolRunner` runs tools with an argument list (no shell) and decodes output in the OEM code page (cp866 on Russian Windows).
- **Settings page** (`SettingsViewModel`, `Views/SettingsView.axaml`, pinned at the bottom of the sidebar as a second nav list `FooterPages`): language and theme
  (`system`/`light`/`dark`, applied by `ThemeService` via `Application.RequestedThemeVariant`, persisted in `AppSettings.Theme`). New settings go here.
  Nav selection: two ListBoxes (`SelectedPage`, `SelectedFooter`) — selecting in one clears the other, and a null selection (ListBox rebuilding) is always ignored.
  Theme-aware colors must come from `App.axaml` theme dictionaries (`PageBackground`, `NavBackground`, `CardBackground`, `CardBorder`), not hard-coded.
- **Admin rights** (three layers): (1) `app.manifest` requests `requireAdministrator` (verified present in the published exe; UAC prompt on a normal launch);
  (2) `Program.Main` → `Elevation.TryRelaunch` relaunches elevated once via `ShellExecute "runas"` when started un-elevated (`dotnet run`, IDE, non-elevated parent);
  the relaunched copy gets `--elevation-attempted` so a failed elevation can't loop, and `--no-elevate` skips the whole thing for debugging; command line is built with
  the unit-tested `CommandLine.Join` (Core); (3) if still not elevated (UAC declined) the app runs read-only: `CanModify` is false, an orange banner with a
  **Elevate privileges** button (`MainViewModel.RestartElevatedCommand`; orange `#D9480F` strip at the very top — the red strip under it is the missing-hardware one) is shown, and the backend's `RequireAdmin` rejects every write anyway.
  `Elevation.TryRelaunch` returns an outcome (Started / Declined / Failed+detail) — never swallow a failure silently (first Windows test: the button "did nothing" and nothing said why).
  Every attempt is logged to `%APPDATA%\NetManager\elevation.log`. The relaunched copy knows it was an attempt (`Elevation.WasRequested`) and, if still un-elevated, the banner shows a
  second line with the most specific reason it can find (priority: exe on a **network location** → `__COMPAT_LAYER` has `RunAsInvoker` → account not in Administrators → generic "Windows ignored the request").
  `TryRelaunch` also strips `RunAsInvoker` from its own environment before relaunching (`CompatLayer`, Core, tested).
  **Real finding from the user's Win 11 ARM VM** (elevation.log): the exe ran from `Z:\NetManager.exe` — a WebDAV-backed VM shared folder (the relaunched copy saw it as
  `\\localhost@9843\DavWWWRoot\...`); `__COMPAT_LAYER` was `DetectorsAppHealth Installer` (NOT RunAsInvoker); `runas` "started" but the new process was un-elevated, no UAC prompt.
  Working theory: elevating from a network share/mapped drive doesn't work (separate elevated logon session, no mapped drives). Fix = run from a local folder; **don't** auto-copy the exe to a user-writable
  folder and run that elevated (privilege-escalation hole). The log line also records `verb=`, `network=`, `adminAccount=`, `uac=[EnableLUA, ConsentPromptBehaviorUser, ConsentPromptBehaviorAdmin]`, user.
  **Second finding (after running from a local folder):** the banner reported the account is *not in the Administrators group* (`Elevation.IsAdminAccount`, SID S-1-5-32-544 in the token groups,
  true even for a UAC-filtered admin token) and Windows showed **no credentials prompt** on "Elevate privileges". A standard account normally gets one, so the usual causes are UAC off
  (`EnableLUA=0`) or policy `ConsentPromptBehaviorUser=0` ("automatically deny elevation requests"); both are read from `HKLM\...\Policies\System` and named in the banner. For standard accounts the banner
  also has **Sign in as administrator…** = `ShellExecute` verb `runasuser` (the "Run as different user" credentials dialog) — unverified on Windows; the app then runs under that admin's profile (own `%APPDATA%`).
  Real fix for the user's VM: use an administrator account (or make this one an admin) / re-enable UAC prompting.
  The published exe's manifest was verified to be in the PE `.rsrc` section (win-x64 and win-arm64), so a launch without a UAC prompt is an environment override, not our build.
  Note: a non-elevated parent that launches the exe with plain CreateProcess gets Windows error 740 before `Main` runs — nothing the app can do; run the IDE as admin.

## Localization (EN/RU)

- All user-visible text lives in `Core/Localization/EnglishStrings.cs` and `RussianStrings.cs` (key → text, `{0}` placeholders). `Loc.T(key, args)` in code,
  `{loc:T some.key}` in XAML (`App/Localization/TExtension.cs`, a plain lookup at XAML-load time). Missing RU falls back to EN, then to the key.
- **Adding a string: add the key to BOTH tables.** `LocalizationTests` fails if keys or `{n}` placeholders differ. Don't hard-code English in models, backends or VMs.
- Language defaults to the OS UI language (ru → Russian, else English), is switchable live on the **Settings page**, and persisted in `settings.json`.
- **How live switching works**: `SettingsViewModel` sets the language and raises `LanguageChanged`; `MainViewModel` handles it **posted to the dispatcher** (the language box
  sits on the page being rebuilt) in `RebuildForLanguage`: rebuilds `Pages`/`FooterPages` (nav titles), then sets `CurrentPage = null` and back to
  the page so the ContentControl **re-creates the view** and every `{loc:T}` / computed string is re-read. Therefore:
  - text that stays on screen across a switch (window chrome: banners, status bar) must be bound to VM properties and re-notified there;
  - computed VM/model strings are fine (re-read when the view is rebuilt);
  - combo-box items must not rely on instance identity of localized objects — use `SelectedValueBinding` on an enum/code (see route filter, language/theme boxes).
- Don't use the `Loc` indexer binding (`{Binding [key], Source=…}`) for live text: it did **not** refresh on Avalonia 12. VM property notification works.
- Russian text is ~30% longer: check layouts (`DataGrid` columns use `Width="Auto"`, nav items wrap).

## Avalonia 12 / MVVM gotchas learned here

- Compiled bindings are on by default (`AvaloniaUseCompiledBindingsByDefault`). Give `DataTemplate`s an `x:DataType`. For parent-command bindings inside templates
  and for `DataGrid`/`SelectedValueBinding` use `x:CompileBindings="False"` with `{Binding $parent[ItemsControl].DataContext.XCommand}`.
- `TextBox.Watermark` is obsolete → `PlaceholderText`.
- `[RelayCommand]` supports **at most one parameter** (a second one doesn't compile).
- `[NotifyPropertyChangedFor(...)]` — double-check the attribute actually landed when scripting edits; a silently unapplied replace left IsVisible bindings dead once.
- Theme brushes: `CardBackgroundFillColorDefaultBrush` etc. don't exist in this Fluent theme → custom brushes `NavBackground/CardBackground/CardBorder` in `App.axaml`.
- Glyphs: avoid emoji for icons (`🖧` rendered as a box); geometric/symbol characters (`▣ ⇄ ★ ⚒ ⚙`) work on the Mac renderer — not yet seen on Windows (Segoe UI Symbol should have them).
- Avalonia has no message box: confirmations are inline panels (see `MaintenanceView`).

## Rider gotchas (the user builds/runs in Rider)

Rider's "ReSharper Build" ("Build with surface heuristics" in its log) runs a **reduced build**, which produced two real failures here:
1. `CS0006` for `obj/.../ref/NetManager.Core.dll` → fixed by `<ProduceReferenceAssembly>false</ProduceReferenceAssembly>` in `Directory.Build.props`. Keep it.
2. Running the app failed (`libhostpolicy.dylib` / missing `NetManager.runtimeconfig.json`): the reduced build left `bin/` with only the apphost, dll and pdb.
   Fix on the user's side: Settings → Build, Execution, Deployment → Toolset and Build → turn off **Use ReSharper Build**; or run `dotnet run --project src/NetManager.App`.
Also: don't run `dotnet build/test` while Rider is building — overlapping builds race on `obj/` and can delete outputs.

## Windows runtime gotchas (found on the first real run, Win 11 ARM64 VM)

- **`BuiltInComInteropSupport` must stay `true`** in `NetManager.App.csproj`. The Avalonia template sets it to `false`, which writes the runtimeconfig switch
  `System.Runtime.InteropServices.BuiltInComInterop.IsSupported=false`; `System.Management` (WMI) is COM-based, so every WMI call then threw
  `TypeInitializationException: The type initializer for 'System.Management.ManagementPath' threw an exception` with the real cause only in the inner exception:
  "Built-in COM has been disabled via a feature switch". (Not a single-file problem — an earlier guess of `IncludeAllContentForSelfExtract` was wrong and was reverted.)
- `AppShell.RunAsync`'s catch-all shows the *whole* exception chain (`DeepMessage`, joined with " → "), not just `ex.Message` — that is what exposed the cause above.
  Don't shorten it back to `ex.Message`.
- Check the published `NetManager.runtimeconfig.json` / bundled config for unexpected feature switches when something works in `dotnet run` but not on Windows.

## Verifying UI without a screen (works on this Mac)

`screencapture` is not permitted, so UI is checked headlessly with Avalonia.Headless + Skia in a **throwaway project outside the repo** (scratchpad `shot/`):
`AppBuilder.Configure<NetManager.App.App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting()`,
create `MainViewModel(new MockNetworkService(), new ProfileStore(tmp), new SettingsStore(tmp), new RecoveryStore(tmp))`, `new MainWindow{DataContext=vm}.Show()`,
drive state from a `DispatcherTimer` (`Dispatcher.UIThread.RunJobs()`, `window.CaptureRenderedFrame()?.Save(png)`), run `Dispatcher.UIThread.MainLoop(token)`; then `Read` the PNGs.
Give async mock operations enough time (mock delays 250 ms per call; "Recreate" needs ~2.5 s). In zsh an unmatched glob (`rm x*.png`) aborts the whole `&&` chain — use `find … -delete`.
Seed `recovery.json` in the temp dir to see the "missing hardware" / "recently removed" sections.

## Conventions

- Match surrounding code: terse, comments explain *why* (safety, Windows quirks), no commentary on what code does. File-scoped namespaces, collection expressions, primary constructors are used.
- Safety-sensitive logic (removal policy, validation) lives in **Core as pure functions with unit tests**; backends enforce it again.
- Backend errors are thrown as `NetworkOperationException(Loc.T(...))`; WMI return codes map through `wmi.<code>` keys (`DescribeWmiError`).
- Mock must mirror real behaviour where the UI depends on it (e.g. it enforces `RemovalPolicy` too), so the demo can't hide logic bugs.
  `MockNetworkService(canModify: false)` also refuses every write like an un-elevated Windows session (tested); preview that state on macOS with `NETMANAGER_DEMO_READONLY=1 dotnet run --project src/NetManager.App`.
- Don't commit unless asked. The repo has a `.git` that appeared during the session (likely created by Rider); nothing has been committed by Claude.

## Unverified on real Windows (check first when the user reports problems)

1. Raw-offset parsing of `MIB_IPFORWARD_ROW2` (garbled route list ⇒ offsets).
2. Gateways: **WMI rejects metric 0 with code 90** (confirmed on Win 11) → "automatic" is sent by omitting the metric parameter (only possible when no gateway has an explicit metric; mixed
   lists store the automatic ones as 1 — UI then reads them back as explicit). Still unverified: that omitting really leaves it automatic; `SetIPConnectionMetric` (only called when the value changed;
   registry fallback for automatic); registry `DefaultGateway`/`DefaultGatewayMetric` as the source of configured gateways and being cleared correctly.
   `ApplyIpv4Async` is **not atomic** (address → gateways → interface metric → DNS): `ConnectionsViewModel.ApplyAsync` refreshes the list after a failure (`RefreshQuietlyAsync`) but keeps the editor and the error.
3. Disabled adapters appearing only via WMI; `ConfigManagerErrorCode == 22` as "disabled"; cable-unplugged/Wi-Fi-off must stay "Not connected", not "Disabled".
4. VirtualBox host-only adapters reporting service `VBoxNetAdp6`/`VBoxNetAdp` and `PhysicalAdapter == false` (otherwise they show as protected).
5. `pnputil /remove-device`, `/scan-devices` exit codes (0 / 3010 = success), `VBoxManage hostonlyif create`, then re-applying IPv4 to the new adapter.
6. Windows scaling (125–150%) with the longer Russian strings.
A smoke-test checklist for the user is in `README.md`.

## Roadmap (not built)

IPv6, Wi-Fi scan/connect (WlanAPI), VPN, proxy, MTU/MAC override, interface metric editing, DNS search suffixes, bridge/VLAN, a UI-level test project.
