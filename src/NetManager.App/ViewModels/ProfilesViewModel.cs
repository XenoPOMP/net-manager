using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetManager.Core.Localization;
using NetManager.Core.Models;
using NetManager.Core.Services;

namespace NetManager.App.ViewModels;

public partial class ProfilesViewModel : ObservableObject
{
    private readonly INetworkService _network;
    private readonly ProfileStore _store;
    private readonly AppShell _shell;

    public ObservableCollection<ConnectionProfile> Profiles { get; } = [];

    /// <summary>Set by the shell: reloads the Connections / Routing pages after profiles changed the system.</summary>
    public Func<Task>? AfterChange { get; set; }


    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedAdapterLine))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand), nameof(DeleteCommand))]
    private ConnectionProfile? _selected;

    public string StoreInfo => Loc.T("profiles.storedIn", _store.FilePath);
    public string SelectedAdapterLine => Loc.T("profiles.adapter", Selected?.AdapterName);

    public ProfilesViewModel(INetworkService network, ProfileStore store, AppShell shell)
    {
        _network = network;
        _store = store;
        _shell = shell;
        foreach (var p in store.Load()) Profiles.Add(p);
        Selected = Profiles.FirstOrDefault();
        // Which profile is "active" is derived from the live system state, so recompute whenever that state was re-read or changed.
        shell.NetworkChanged += () => _ = RefreshActiveAsync();
    }

    private bool _activeBusy, _activeDirty;

    /// <summary>A profile is active exactly while the adapter's current settings match it; change anything by hand and the marker goes away.</summary>
    public async Task RefreshActiveAsync()
    {
        if (_activeBusy) { _activeDirty = true; return; }   // collapse bursts of NetworkChanged into one more pass
        _activeBusy = true;
        try
        {
            do
            {
                _activeDirty = false;
                try
                {
                    var adapters = await _network.GetAdaptersAsync();
                    var routes = await _network.GetRoutesAsync();
                    foreach (var p in Profiles) p.IsActive = ProfileMatcher.IsActive(p, adapters, routes);
                }
                catch (Exception) { foreach (var p in Profiles) p.IsActive = false; }
            } while (_activeDirty);
        }
        finally { _activeBusy = false; }
    }

    /// <summary>Saves (or overwrites) a profile from the editor state plus the adapter's current static routes.</summary>
    public async Task SaveAsync(string name, AdapterInfo adapter, Ipv4Config config)
    {
        var routes = new List<ProfileRoute>();
        try
        {
            routes = (await _network.GetRoutesAsync())
                // not the default route: a default gateway is part of the IPv4 settings (Gateways), not a separate static route
                .Where(r => r.Kind == RouteKind.Static && r.InterfaceIndex == adapter.InterfaceIndex && !r.IsDefault)
                .Select(r => new ProfileRoute { Destination = r.Destination, PrefixLength = r.PrefixLength, NextHop = r.NextHop, Metric = r.Metric, Persistent = r.IsPersistent })
                .ToList();
        }
        catch (Exception) { /* routes are optional; save the address settings anyway */ }

        var profile = new ConnectionProfile
        {
            Name = name, AdapterName = adapter.Name, AdapterMac = adapter.MacAddress, Ipv4 = config.Clone(), Routes = routes,
        };
        int i = Profiles.ToList().FindIndex(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) Profiles[i] = profile; else Profiles.Add(profile);
        Persist();
        Selected = profile;
        _shell.Info(Loc.T("profiles.saved", name, routes.Count));
    }

    private void Persist()
    {
        try { _store.Save(Profiles); }
        catch (Exception ex) { _shell.Error(Loc.T("profiles.writeFail", _store.FilePath, ex.Message)); }
    }

    private bool HasSelection => Selected is not null;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task ApplyAsync()
    {
        if (Selected is not { } p) return;
        await _shell.RunAsync(Loc.T("profiles.activating", p.Name), async () =>
        {
            var adapters = await _network.GetAdaptersAsync();
            var adapter = ProfileStore.FindAdapter(p, adapters)
                ?? throw new NetworkOperationException(Loc.T("profiles.noAdapter", p.AdapterName));

            await _network.ApplyIpv4Async(adapter.InterfaceIndex, p.Ipv4);

            var failures = new List<string>();
            foreach (var r in p.Routes)
            {
                try
                {
                    await _network.AddRouteAsync(new RouteRequest
                    {
                        Destination = r.Destination, PrefixLength = r.PrefixLength, NextHop = r.NextHop,
                        InterfaceIndex = adapter.InterfaceIndex, Metric = r.Metric, Persistent = r.Persistent,
                    });
                }
                catch (NetworkOperationException ex) when (ex.Kind == NetworkErrorKind.AlreadyExists) { /* already there: that is the goal */ }
                catch (NetworkOperationException ex) { failures.Add($"{r.Destination}/{r.PrefixLength}: {ex.Message}"); }
            }
            if (failures.Count > 0) throw new NetworkOperationException(Loc.T("profiles.routesFailed", string.Join("; ", failures)));
        }, Loc.T("profiles.active", p.Name));
        // Even a partly failed activation changed things: show the real state on the other pages (without replacing the message above).
        if (AfterChange is { } refresh) await refresh();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Delete()
    {
        if (Selected is not { } p) return;
        Profiles.Remove(p);
        Persist();
        Selected = Profiles.FirstOrDefault();
        _shell.Info(Loc.T("profiles.deleted", p.Name));
    }
}
