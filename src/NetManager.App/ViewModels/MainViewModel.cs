using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetManager.Core.Localization;
using NetManager.Core.Services;

namespace NetManager.App.ViewModels;

public sealed record NavItem(string TitleKey, string Glyph, ObservableObject Page)
{
    public string Title => Loc.T(TitleKey);
}

public partial class MainViewModel : ObservableObject
{
    public AppShell Shell { get; } = new();
    public INetworkService Network { get; }
    public SettingsViewModel Settings { get; }

    /// <summary>The main pages (top of the sidebar) and the pinned ones (bottom: Settings). Only one of the two lists has a selection.</summary>
    [ObservableProperty] private IReadOnlyList<NavItem> _pages;
    [ObservableProperty] private IReadOnlyList<NavItem> _footerPages;
    [ObservableProperty] private NavItem? _selectedPage;
    [ObservableProperty] private NavItem? _selectedFooter;
    /// <summary>What the content area shows. Cleared and set again on a language change to rebuild the view.</summary>
    [ObservableProperty] private ObservableObject? _currentPage;

    public string Backend => Network.BackendName;
    /// <summary>Right end of the status bar: the backend and which build this is.</summary>
    public string StatusRight => $"{Backend} · {BuildInfo.Text}";
    public string AdminBanner => Loc.T("banner.admin");
    public string AdminButton => Loc.T("banner.restartAdmin");
    /// <summary>Second banner line. Only present when there is something concrete to say about why we are still not elevated.</summary>
    public string AdminHint =>
        Elevation.RunsFromNetworkLocation ? Loc.T("banner.admin.network", Environment.ProcessPath ?? "")
        : Elevation.CompatLayerBlocksElevation ? Loc.T("banner.admin.invoker")
        : !Elevation.IsAdminAccount && Elevation.UacIsOff ? Loc.T("banner.admin.uacOff")
        : !Elevation.IsAdminAccount && Elevation.UacDeniesStandardUsers ? Loc.T("banner.admin.uacDeny")
        : !Elevation.IsAdminAccount ? Loc.T("banner.admin.standard")
        : Elevation.WasRequested ? Loc.T("banner.admin.attempted")
        : "";
    public bool HasAdminHint => AdminHint.Length > 0;
    /// <summary>A standard account can't elevate itself; offer the "enter an administrator's credentials" route.</summary>
    public bool ShowRunAsAdminUser => !Elevation.IsAdminAccount;
    public string RunAsAdminUserButton => Loc.T("banner.runAsAdmin");
    public bool ShowAdminWarning => !Network.CanModify;
    public MaintenanceViewModel Maintenance { get; }
    public ProfilesViewModel Profiles { get; }
    public bool ShowMissingBanner => Maintenance.HasMissing;
    public string MissingBannerText => Loc.T("banner.missing", Maintenance.MissingNames);
    public string MissingBannerButton => Loc.T("banner.missingOpen");

    public MainViewModel(INetworkService network, ProfileStore store, SettingsStore settings, RecoveryStore recovery)
    {
        Network = network;
        Settings = new SettingsViewModel(settings);
        // Posted, not run inline: the language box lives on the page that is about to be rebuilt.
        Settings.LanguageChanged += () => Dispatcher.UIThread.Post(RebuildForLanguage);

        var profiles = Profiles = new ProfilesViewModel(network, store, Shell);
        var connections = new ConnectionsViewModel(network, profiles, Shell);
        var routes = new RoutesViewModel(network, Shell);
        // Anything that adds or removes adapters must refresh the other pages too.
        Maintenance = new MaintenanceViewModel(network, recovery, Shell, async () => { await connections.RefreshAsync(); await routes.RefreshAsync(); });
        Maintenance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MaintenanceViewModel.HasMissing) or nameof(MaintenanceViewModel.MissingNames))
            {
                OnPropertyChanged(nameof(ShowMissingBanner));
                OnPropertyChanged(nameof(MissingBannerText));
            }
        };
        // Activating a profile changes adapters and routes: reload those pages now, and again shortly after — an address that comes
        // from DHCP, or an adapter that is still re-initialising, is often not there yet in the first read.
        profiles.AfterChange = async () =>
        {
            await connections.ReloadAsync(keepEditor: false);
            await routes.ReloadAsync();
            _ = FollowUpReloadsAsync(connections, routes);
        };
        _pages = BuildPages(connections, routes, profiles, Maintenance);
        _footerPages = [new("nav.settings", "⚙", Settings)];
        SelectedPage = Pages[0];

        _ = connections.RefreshAsync();
        _ = routes.RefreshAsync();
        _ = Maintenance.RefreshAsync();
    }

    private static IReadOnlyList<NavItem> BuildPages(ObservableObject connections, ObservableObject routes, ObservableObject profiles, ObservableObject maintenance) =>
    [
        new("nav.connections", "▣", connections),
        new("nav.routes", "⇄", routes),
        new("nav.profiles", "★", profiles),
        new("nav.maintenance", "⚒", maintenance),
    ];

    // The nav ListBoxes report null while their items are replaced, and selecting in one list clears the other: nulls never change the page.
    private static async Task FollowUpReloadsAsync(ConnectionsViewModel connections, RoutesViewModel routes)
    {
        foreach (var delay in new[] { 2000, 3000 })
        {
            await Task.Delay(delay);
            await connections.ReloadAsync(keepEditor: true);   // keep anything the user started typing meanwhile
            await routes.ReloadAsync();
        }
    }

    partial void OnSelectedPageChanged(NavItem? value)
    {
        if (value is null) return;
        CurrentPage = value.Page;
        SelectedFooter = null;
        if (ReferenceEquals(value.Page, Profiles)) _ = Profiles.RefreshActiveAsync();   // never show a stale "active" marker
    }

    partial void OnSelectedFooterChanged(NavItem? value)
    {
        if (value is null) return;
        CurrentPage = value.Page;
        SelectedPage = null;
    }

    private void RebuildForLanguage()
    {
        var page = CurrentPage;
        Pages = [.. Pages.Select(p => p with { })];               // new NavItem instances → the lists re-read Title
        FooterPages = [.. FooterPages.Select(p => p with { })];
        var top = Pages.FirstOrDefault(p => ReferenceEquals(p.Page, page));
        var foot = FooterPages.FirstOrDefault(p => ReferenceEquals(p.Page, page));
        if (top is not null) SelectedPage = top;
        else if (foot is not null) SelectedFooter = foot;
        CurrentPage = null;                                        // tear the page view down …
        CurrentPage = page;                                        // … and build it again in the new language
        OnPropertyChanged(nameof(Backend));
        OnPropertyChanged(nameof(StatusRight));
        OnPropertyChanged(nameof(AdminBanner));
        OnPropertyChanged(nameof(AdminButton));
        OnPropertyChanged(nameof(AdminHint));
        OnPropertyChanged(nameof(RunAsAdminUserButton));
        OnPropertyChanged(nameof(MissingBannerText));
        OnPropertyChanged(nameof(MissingBannerButton));
        Shell.Info(Loc.T("status.ready"));
    }

    /// <summary>Starts an elevated copy (UAC prompt) and closes this one; if the prompt is declined or fails nothing changes and the status bar says why.</summary>
    [RelayCommand]
    private Task RestartElevatedAsync() => RelaunchAsync("runas");

    /// <summary>Asks for an administrator's user name and password ("Run as different user"), for a standard account.</summary>
    [RelayCommand]
    private Task RunAsAdminUserAsync() => RelaunchAsync("runasuser");

    private async Task RelaunchAsync(string verb)
    {
        Shell.Info(Loc.T("elevate.requesting"));
        var args = Environment.GetCommandLineArgs().Skip(1).ToList();
        // ShellExecute blocks until the dialog is answered — keep that off the UI thread.
        var outcome = await Task.Run(() => Elevation.TryRelaunch(args, markAttempted: true, verb));
        switch (outcome.Result)
        {
            case RelaunchResult.Started:
                (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
                break;
            case RelaunchResult.Declined:
                Shell.Error(Loc.T("err.elevationDeclined"));
                break;
            default:
                Shell.Error(Loc.T("err.elevationFailed", outcome.Detail ?? "?"));
                break;
        }
    }

    [RelayCommand]
    private void OpenMaintenance() => SelectedPage = Pages.First(p => ReferenceEquals(p.Page, Maintenance));
}
