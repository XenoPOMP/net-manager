using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetManager.Core.Localization;
using NetManager.Core.Models;
using NetManager.Core.Services;

namespace NetManager.App.ViewModels;

public partial class ConnectionsViewModel : ObservableObject
{
    private readonly INetworkService _network;
    private readonly ProfilesViewModel _profiles;
    private readonly AppShell _shell;

    public ObservableCollection<AdapterInfo> Adapters { get; } = [];
    public ObservableCollection<AddressRow> Addresses { get; } = [];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSelection), nameof(SelectedIsDisabled), nameof(SelectedIsEnabled), nameof(CurrentLeaseText))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand), nameof(RevertCommand), nameof(ToggleEnabledCommand), nameof(RenewLeaseCommand), nameof(SaveProfileCommand))]
    private AdapterInfo? _selected;

    // ---- editor state ("IPv4" tab)
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsAutomatic))] private bool _isManual;
    [ObservableProperty] private string _gateway = "";
    [ObservableProperty] private bool _dnsAutomatic = true;
    [ObservableProperty] private string _dnsText = "";
    [ObservableProperty] private string _profileName = "";

    public bool HasSelection => Selected is not null;
    public bool SelectedIsDisabled => Selected?.IsDisabled == true;
    public bool SelectedIsEnabled => Selected is { IsDisabled: false };
    public bool IsAutomatic { get => !IsManual; set => IsManual = !value; }
    public string ToggleEnabledText => Loc.T(Selected?.IsEnabled == false ? "conn.enableAdapter" : "conn.disableAdapter");
    public string CurrentLeaseText => Loc.T("conn.currentLease", Selected?.PrimaryAddress);

    public ConnectionsViewModel(INetworkService network, ProfilesViewModel profiles, AppShell shell)
    {
        _network = network;
        _profiles = profiles;
        _shell = shell;
    }

    partial void OnSelectedChanged(AdapterInfo? value)
    {
        OnPropertyChanged(nameof(ToggleEnabledText));
        LoadEditor(value);
    }

    private bool _loading;

    partial void OnIsManualChanged(bool value)
    {
        if (_loading) return;
        // Switching to Manual with an empty table: start from the addresses currently in use.
        if (value && Addresses.Count == 0 && Selected is { } a)
        {
            foreach (var e in a.Ipv4.Addresses.Where(x => !x.Address.StartsWith("169.254.")))
                Addresses.Add(new AddressRow(e.Address, e.Mask));
            if (Addresses.Count == 0) Addresses.Add(new AddressRow());
            if (string.IsNullOrEmpty(Gateway)) Gateway = a.Ipv4.Gateway ?? "";
        }
    }

    private void LoadEditor(AdapterInfo? a)
    {
        Addresses.Clear();
        if (a is null) return;
        var c = a.Ipv4;
        _loading = true;
        try
        {
            IsManual = c.Method == Ipv4Method.Manual;
            if (IsManual)
                foreach (var e in c.Addresses) Addresses.Add(new AddressRow(e.Address, e.Mask));
            Gateway = IsManual ? c.Gateway ?? "" : "";
            DnsAutomatic = c.DnsAutomatic;
            DnsText = string.Join(Environment.NewLine, c.DnsServers);
            ProfileName = a.Name;
        }
        finally { _loading = false; }
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IReadOnlyList<AdapterInfo> list = [];
        var ok = await _shell.RunAsync(Loc.T("conn.reading"), async () => list = await _network.GetAdaptersAsync(), Loc.T("conn.refreshed"));
        if (!ok) return;

        string? keep = Selected?.Id;
        Adapters.Clear();
        foreach (var a in list) Adapters.Add(a);
        Selected = Adapters.FirstOrDefault(a => a.Id == keep) ?? Adapters.FirstOrDefault();
    }

    /// <summary>Builds a config from the editor fields, or reports why it can't.</summary>
    private Ipv4Config? BuildConfig()
    {
        var cfg = new Ipv4Config
        {
            Method = IsManual ? Ipv4Method.Manual : Ipv4Method.Automatic,
            Gateway = IsManual && !string.IsNullOrWhiteSpace(Gateway) ? Gateway.Trim() : null,
            DnsAutomatic = DnsAutomatic,
            DnsServers = DnsText.Split(['\n', '\r', ',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
        };
        if (IsManual)
        {
            foreach (var row in Addresses.Where(r => !string.IsNullOrWhiteSpace(r.Address) || !string.IsNullOrWhiteSpace(r.Mask)))
            {
                if (!Ipv4Util.TryParsePrefixOrMask(row.Mask, out var prefix))
                {
                    _shell.Error(Loc.T("conn.badMask", row.Mask));
                    return null;
                }
                cfg.Addresses.Add(new Ipv4AddressEntry(row.Address.Trim(), prefix));
            }
        }
        var problems = Ipv4Util.Validate(cfg);
        if (problems.Count > 0) { _shell.Error(problems[0]); return null; }
        return cfg;
    }

    private bool HasAdapter => Selected is not null;

    [RelayCommand(CanExecute = nameof(HasAdapter))]
    private async Task ApplyAsync()
    {
        if (Selected is not { } a || BuildConfig() is not { } cfg) return;
        if (await _shell.RunAsync(Loc.T("conn.applying", a.Name), () => _network.ApplyIpv4Async(a.InterfaceIndex, cfg), Loc.T("conn.applied", a.Name)))
            await RefreshAsync();
    }

    [RelayCommand(CanExecute = nameof(HasAdapter))]
    private void Revert()
    {
        LoadEditor(Selected);
        _shell.Info(Loc.T("conn.discarded"));
    }

    [RelayCommand(CanExecute = nameof(HasAdapter))]
    private Task ToggleEnabledAsync() => Selected is { } a ? SetEnabledAsync(a, !a.IsEnabled) : Task.CompletedTask;

    /// <summary>Per-row switch in the adapter list: flips the state of that adapter.</summary>
    [RelayCommand]
    private Task ToggleAdapterAsync(AdapterInfo? adapter) => adapter is null ? Task.CompletedTask : SetEnabledAsync(adapter, !adapter.IsEnabled);

    private async Task SetEnabledAsync(AdapterInfo adapter, bool target)
    {

        var ok = await _shell.RunAsync(Loc.T(target ? "conn.enabling" : "conn.disabling", adapter.Name),
            () => _network.SetAdapterEnabledAsync(adapter.Id, target),
            Loc.T(target ? "conn.enabled" : "conn.disabled", adapter.Name));
        // Windows needs a moment to bring a re-enabled adapter up before it reports addresses.
        if (ok && target) await Task.Delay(2000);
        // Refresh even on failure so a switch that was flipped in the UI snaps back to the real state.
        await RefreshAsync();
    }

    [RelayCommand(CanExecute = nameof(HasAdapter))]
    private async Task RenewLeaseAsync()
    {
        if (Selected is not { } a) return;
        if (await _shell.RunAsync(Loc.T("conn.renewing", a.Name), () => _network.RenewDhcpLeaseAsync(a.InterfaceIndex)))
            await RefreshAsync();
    }

    [RelayCommand]
    private Task FlushDnsAsync() => _shell.RunAsync(Loc.T("conn.flushing"), () => _network.FlushDnsCacheAsync(), Loc.T("conn.flushed"));

    [RelayCommand]
    private void AddAddress() => Addresses.Add(new AddressRow());

    [RelayCommand]
    private void RemoveAddress(AddressRow? row)
    {
        if (row is not null) Addresses.Remove(row);
    }

    [RelayCommand(CanExecute = nameof(HasAdapter))]
    private async Task SaveProfileAsync()
    {
        if (Selected is not { } a || BuildConfig() is not { } cfg) return;
        if (string.IsNullOrWhiteSpace(ProfileName)) { _shell.Error(Loc.T("conn.needName")); return; }
        await _profiles.SaveAsync(ProfileName.Trim(), a, cfg);
    }
}
