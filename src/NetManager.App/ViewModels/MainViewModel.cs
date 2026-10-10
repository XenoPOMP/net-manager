using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetManager.Core.Localization;
using NetManager.Core.Services;

namespace NetManager.App.ViewModels;

public sealed record NavItem(string TitleKey, string Glyph, ObservableObject Page)
{
    public string Title => Loc.T(TitleKey);
}

/// <summary>Language names are shown in their own language, so they are not part of the translation tables.</summary>
public sealed record LanguageOption(string Code, string Name);

public partial class MainViewModel : ObservableObject
{
    private readonly SettingsStore _settings;

    public AppShell Shell { get; } = new();
    public INetworkService Network { get; }
    public static IReadOnlyList<LanguageOption> Languages { get; } = [new("en", "English"), new("ru", "Русский")];

    [ObservableProperty] private IReadOnlyList<NavItem> _pages;
    [ObservableProperty] private NavItem? _selectedPage;
    /// <summary>What the content area shows. Cleared and set again on a language change to rebuild the view.</summary>
    [ObservableProperty] private ObservableObject? _currentPage;
    [ObservableProperty] private string _selectedLanguage;

    public string Backend => Network.BackendName;
    public string LanguageLabel => Loc.T("nav.language");
    public string AdminBanner => Loc.T("banner.admin");
    public bool ShowAdminWarning => !Network.CanModify;
    public MaintenanceViewModel Maintenance { get; }
    public bool ShowMissingBanner => Maintenance.HasMissing;
    public string MissingBannerText => Loc.T("banner.missing", Maintenance.MissingNames);
    public string MissingBannerButton => Loc.T("banner.missingOpen");

    public MainViewModel(INetworkService network, ProfileStore store, SettingsStore settings, RecoveryStore recovery)
    {
        Network = network;
        _settings = settings;
        _selectedLanguage = Loc.Language;

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
        new("nav.maintenance", "⚙", maintenance),
    ];

    partial void OnSelectedPageChanged(NavItem? value)
    {
        // The nav ListBox reports null while its items are being replaced; keep showing the current page then.
        if (value is not null) CurrentPage = value.Page;
    }

    partial void OnSelectedLanguageChanged(string value)
    {
        if (value == Loc.Language) return;
        Loc.SetLanguage(value);
        try { _settings.Save(new AppSettings { Language = Loc.Language }); } catch (IOException) { /* not critical */ }

        // Rebuild everything that was rendered with the old language.
        int index = Math.Max(0, Pages.ToList().FindIndex(p => ReferenceEquals(p.Page, CurrentPage)));
        var page = CurrentPage;
        Pages = [.. Pages.Select(p => p with { })];   // new NavItem instances → the list re-reads Title
        SelectedPage = Pages[index];
        CurrentPage = null;                           // tear the page view down …
        CurrentPage = page;                           // … and build it again in the new language
        OnPropertyChanged(nameof(Backend));
        OnPropertyChanged(nameof(LanguageLabel));
        OnPropertyChanged(nameof(AdminBanner));
        OnPropertyChanged(nameof(MissingBannerText));
        OnPropertyChanged(nameof(MissingBannerButton));
        Shell.Info(Loc.T("status.ready"));
    }

    [RelayCommand]
    private void OpenMaintenance() => SelectedPage = Pages.First(p => ReferenceEquals(p.Page, Maintenance));
}
