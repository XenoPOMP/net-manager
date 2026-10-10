using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetManager.Core.Models;
using NetManager.Core.Services;

namespace NetManager.App.ViewModels;

public sealed record InterfaceChoice(int? Index, string Label)
{
    public override string ToString() => Label;
}

public partial class RoutesViewModel : ObservableObject
{
    private readonly INetworkService _network;
    private readonly AppShell _shell;
    private IReadOnlyList<RouteEntry> _all = [];

    public static IReadOnlyList<string> Filters { get; } = ["All", "Static", "Dynamic", "System"];
    public ObservableCollection<InterfaceChoice> Interfaces { get; } = [];

    [ObservableProperty] private IReadOnlyList<RouteEntry> _routes = [];
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand))] private RouteEntry? _selectedRoute;
    [ObservableProperty] private string _selectedFilter = "All";
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _countText = "";

    // ---- "new static route" form
    [ObservableProperty] private string _newDestination = "";
    [ObservableProperty] private string _newMask = "255.255.255.0";
    [ObservableProperty] private string _newGateway = "";
    [ObservableProperty] private string _newMetric = "1";
    [ObservableProperty] private InterfaceChoice? _newInterface;
    [ObservableProperty] private bool _newPersistent = true;

    public RoutesViewModel(INetworkService network, AppShell shell)
    {
        _network = network;
        _shell = shell;
        Interfaces.Add(new InterfaceChoice(null, "Automatic (by gateway)"));
        _newInterface = Interfaces[0];
    }

    partial void OnSelectedFilterChanged(string value) => ApplyFilter();
    partial void OnSearchChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        IEnumerable<RouteEntry> q = _all;
        q = SelectedFilter switch
        {
            "Static" => q.Where(r => r.Kind == RouteKind.Static),
            "Dynamic" => q.Where(r => r.Kind == RouteKind.Dynamic),
            "System" => q.Where(r => r.Kind == RouteKind.System),
            _ => q,
        };
        if (!string.IsNullOrWhiteSpace(Search))
        {
            var s = Search.Trim();
            q = q.Where(r => r.DestinationCidr.Contains(s, StringComparison.OrdinalIgnoreCase)
                || (r.NextHop?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false)
                || r.InterfaceName.Contains(s, StringComparison.OrdinalIgnoreCase)
                || r.Protocol.Contains(s, StringComparison.OrdinalIgnoreCase));
        }
        Routes = q.ToList();
        CountText = $"{Routes.Count} of {_all.Count} routes";
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IReadOnlyList<RouteEntry> list = [];
        IReadOnlyList<AdapterInfo> adapters = [];
        var ok = await _shell.RunAsync("Reading routing table", async () =>
        {
            list = await _network.GetRoutesAsync();
            adapters = await _network.GetAdaptersAsync();
        }, "Routing table refreshed");
        if (!ok) return;

        _all = list;
        var chosen = NewInterface?.Index;
        while (Interfaces.Count > 1) Interfaces.RemoveAt(1);
        foreach (var a in adapters.Where(a => a.IsEnabled && a.Kind != AdapterKind.Tunnel))
            Interfaces.Add(new InterfaceChoice(a.InterfaceIndex, $"{a.Name} (#{a.InterfaceIndex})"));
        NewInterface = Interfaces.FirstOrDefault(i => i.Index == chosen) ?? Interfaces[0];
        ApplyFilter();
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        if (!Ipv4Util.IsValidAddress(NewDestination)) { _shell.Error("Destination must be a valid IPv4 address, e.g. 10.8.0.0."); return; }
        if (!Ipv4Util.TryParsePrefixOrMask(NewMask, out var prefix)) { _shell.Error("Netmask must be a mask (255.255.255.0) or a prefix length (24)."); return; }
        string? hop = string.IsNullOrWhiteSpace(NewGateway) ? null : NewGateway.Trim();
        if (hop is not null && !Ipv4Util.IsValidAddress(hop)) { _shell.Error("Gateway must be a valid IPv4 address."); return; }
        if (hop is null && NewInterface?.Index is null) { _shell.Error("Choose an interface for an on-link route (no gateway)."); return; }
        if (!int.TryParse(NewMetric, out var metric) || metric is < 1 or > 9999) { _shell.Error("Metric must be a number from 1 to 9999."); return; }
        if (NewPersistent && hop is null) { _shell.Error("Persistent routes need a gateway."); return; }

        var req = new RouteRequest
        {
            Destination = NewDestination.Trim(), PrefixLength = prefix, NextHop = hop,
            InterfaceIndex = NewInterface?.Index, Metric = metric, Persistent = NewPersistent,
        };
        if (await _shell.RunAsync($"Adding route {req.Destination}/{req.PrefixLength}", () => _network.AddRouteAsync(req), $"Route {req.Destination}/{req.PrefixLength} added"))
        {
            NewDestination = "";
            await RefreshAsync();
        }
    }

    private bool CanDelete => SelectedRoute?.CanDelete == true;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteSelectedAsync()
    {
        if (SelectedRoute is not { } r) return;
        if (await _shell.RunAsync($"Deleting route {r.DestinationCidr}", () => _network.DeleteRouteAsync(r), $"Route {r.DestinationCidr} deleted"))
            await RefreshAsync();
    }
}
