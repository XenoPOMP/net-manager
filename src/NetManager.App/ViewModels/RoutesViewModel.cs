using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetManager.Core.Localization;
using NetManager.Core.Models;
using NetManager.Core.Services;

namespace NetManager.App.ViewModels;

/// <summary>An entry of the interface combo box; the "automatic" entry (Index == null) is translated on the fly.</summary>
public sealed record InterfaceChoice(int? Index, string Name)
{
    public override string ToString() => Index is null ? Loc.T("routes.autoIface") : Name;
}

public enum RouteFilter { All, Static, Dynamic, System }

public sealed record FilterOption(RouteFilter Value)
{
    public string Label => Loc.T($"routes.filter.{Value}");
}

public partial class RoutesViewModel : ObservableObject
{
    private readonly INetworkService _network;
    private readonly AppShell _shell;
    private IReadOnlyList<RouteEntry> _all = [];

    public static IReadOnlyList<FilterOption> Filters { get; } = Enum.GetValues<RouteFilter>().Select(v => new FilterOption(v)).ToList();
    public ObservableCollection<InterfaceChoice> Interfaces { get; } = [];

    [ObservableProperty] private IReadOnlyList<RouteEntry> _routes = [];
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand))] private RouteEntry? _selectedRoute;
    [ObservableProperty] private RouteFilter _filter = RouteFilter.All;
    [ObservableProperty] private string _search = "";
    public string CountText => Loc.T("routes.count", Routes.Count, _all.Count);

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
        Interfaces.Add(new InterfaceChoice(null, ""));
        _newInterface = Interfaces[0];
    }

    partial void OnFilterChanged(RouteFilter value) => ApplyFilter();
    partial void OnSearchChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        IEnumerable<RouteEntry> q = _all;
        q = Filter switch
        {
            RouteFilter.Static => q.Where(r => r.Kind == RouteKind.Static),
            RouteFilter.Dynamic => q.Where(r => r.Kind == RouteKind.Dynamic),
            RouteFilter.System => q.Where(r => r.Kind == RouteKind.System),
            _ => q,
        };
        if (!string.IsNullOrWhiteSpace(Search))
        {
            var s = Search.Trim();
            q = q.Where(r => r.DestinationCidr.Contains(s, StringComparison.OrdinalIgnoreCase)
                || (r.NextHop?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false)
                || r.InterfaceName.Contains(s, StringComparison.OrdinalIgnoreCase)
                || r.Protocol.Contains(s, StringComparison.OrdinalIgnoreCase)
                || r.ProtocolDisplay.Contains(s, StringComparison.OrdinalIgnoreCase));
        }
        Routes = q.ToList();
        OnPropertyChanged(nameof(CountText));
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IReadOnlyList<RouteEntry> list = [];
        IReadOnlyList<AdapterInfo> adapters = [];
        var ok = await _shell.RunAsync(Loc.T("routes.reading"), async () =>
        {
            list = await _network.GetRoutesAsync();
            adapters = await _network.GetAdaptersAsync();
        }, Loc.T("routes.refreshed"));
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
        if (!Ipv4Util.IsValidAddress(NewDestination)) { _shell.Error(Loc.T("routes.err.dest")); return; }
        if (!Ipv4Util.TryParsePrefixOrMask(NewMask, out var prefix)) { _shell.Error(Loc.T("routes.err.mask")); return; }
        string? hop = string.IsNullOrWhiteSpace(NewGateway) ? null : NewGateway.Trim();
        if (hop is not null && !Ipv4Util.IsValidAddress(hop)) { _shell.Error(Loc.T("routes.err.gw")); return; }
        if (hop is null && NewInterface?.Index is null) { _shell.Error(Loc.T("routes.err.iface")); return; }
        if (!int.TryParse(NewMetric, out var metric) || metric is < 1 or > 9999) { _shell.Error(Loc.T("routes.err.metric")); return; }
        if (NewPersistent && hop is null) { _shell.Error(Loc.T("routes.err.persistGw")); return; }

        var req = new RouteRequest
        {
            Destination = NewDestination.Trim(), PrefixLength = prefix, NextHop = hop,
            InterfaceIndex = NewInterface?.Index, Metric = metric, Persistent = NewPersistent,
        };
        if (await _shell.RunAsync(Loc.T("routes.adding", $"{req.Destination}/{req.PrefixLength}"), () => _network.AddRouteAsync(req), Loc.T("routes.added", $"{req.Destination}/{req.PrefixLength}")))
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
        if (await _shell.RunAsync(Loc.T("routes.deleting", r.DestinationCidr), () => _network.DeleteRouteAsync(r), Loc.T("routes.deleted", r.DestinationCidr)))
            await RefreshAsync();
    }
}
