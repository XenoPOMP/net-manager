using CommunityToolkit.Mvvm.ComponentModel;
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

    public MainViewModel(INetworkService network, ProfileStore store, SettingsStore settings)
    {
        Network = network;
        _settings = settings;
        _selectedLanguage = Loc.Language;

        var profiles = new ProfilesViewModel(network, store, Shell);
        var connections = new ConnectionsViewModel(network, profiles, Shell);
        var routes = new RoutesViewModel(network, Shell);
        _pages = BuildPages(connections, routes, profiles);
        SelectedPage = Pages[0];

        _ = connections.RefreshAsync();
        _ = routes.RefreshAsync();
    }

    private static IReadOnlyList<NavItem> BuildPages(ObservableObject connections, ObservableObject routes, ObservableObject profiles) =>
    [
        new("nav.connections", "▣", connections),
        new("nav.routes", "⇄", routes),
        new("nav.profiles", "★", profiles),
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
        Shell.Info(Loc.T("status.ready"));
    }
}
