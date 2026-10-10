using CommunityToolkit.Mvvm.ComponentModel;
using NetManager.Core.Services;

namespace NetManager.App.ViewModels;

public sealed record NavItem(string Title, string Glyph, ObservableObject Page);

public partial class MainViewModel : ObservableObject
{
    public AppShell Shell { get; } = new();
    public INetworkService Network { get; }
    public IReadOnlyList<NavItem> Pages { get; }

    [ObservableProperty] private NavItem _selectedPage;

    public string Backend => Network.BackendName;
    public bool ShowAdminWarning => !Network.CanModify;

    public MainViewModel(INetworkService network, ProfileStore store)
    {
        Network = network;
        var profiles = new ProfilesViewModel(network, store, Shell);
        var connections = new ConnectionsViewModel(network, profiles, Shell);
        var routes = new RoutesViewModel(network, Shell);
        Pages =
        [
            new("Connections", "▣", connections),
            new("Routing table", "⇄", routes),
            new("Profiles", "★", profiles),
        ];
        _selectedPage = Pages[0];

        _ = connections.RefreshAsync();
        _ = routes.RefreshAsync();
    }
}
