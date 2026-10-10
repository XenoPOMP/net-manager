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
    public string AdminBanner => Loc.T("banner.admin");
    public string AdminButton => Loc.T("banner.restartAdmin");
    public bool ShowAdminWarning => !Network.CanModify;
    public MaintenanceViewModel Maintenance { get; }
    public bool ShowMissingBanner => Maintenance.HasMissing;
    public string MissingBannerText => Loc.T("banner.missing", Maintenance.MissingNames);
    public string MissingBannerButton => Loc.T("banner.missingOpen");

    public MainViewModel(INetworkService network, ProfileStore store, SettingsStore settings, RecoveryStore recovery)
    {
        Network = network;
        Settings = new SettingsViewModel(settings);
        // Posted, not run inline: the language box lives on the page that is about to be rebuilt.
        Settings.LanguageChanged += () => Dispatcher.UIThread.Post(RebuildForLanguage);

        var profiles = new ProfilesViewModel(network, store, Shell);
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
    partial void OnSelectedPageChanged(NavItem? value)
    {
        if (value is null) return;
        CurrentPage = value.Page;
        SelectedFooter = null;
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
        OnPropertyChanged(nameof(AdminBanner));
        OnPropertyChanged(nameof(AdminButton));
        OnPropertyChanged(nameof(MissingBannerText));
        OnPropertyChanged(nameof(MissingBannerButton));
        Shell.Info(Loc.T("status.ready"));
    }

    /// <summary>Starts an elevated copy (UAC prompt) and closes this one; if the prompt is declined nothing changes.</summary>
    [RelayCommand]
    private void RestartElevated()
    {
        if (!Elevation.TryRelaunch(Environment.GetCommandLineArgs().Skip(1), markAttempted: false))
        {
            Shell.Error(Loc.T("err.elevationDeclined"));
            return;
        }
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    [RelayCommand]
    private void OpenMaintenance() => SelectedPage = Pages.First(p => ReferenceEquals(p.Page, Maintenance));
}
