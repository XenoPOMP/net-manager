using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetManager.Core.Localization;
using NetManager.Core.Models;
using NetManager.Core.Services;

namespace NetManager.App.ViewModels;

/// <summary>A removable virtual adapter with a check box.</summary>
public partial class RemovableItem(AdapterInfo adapter) : ObservableObject
{
    public AdapterInfo Adapter { get; } = adapter;
    [ObservableProperty] private bool _isChecked;
    public string Title => Adapter.Description is { Length: > 0 } d ? d : Adapter.Name;
    public string Subtitle => $"{Adapter.Name} · {Adapter.PrimaryAddress} · {Adapter.StatusText}";
}

public sealed class RemovedItem(RemovedAdapterRecord record, bool canRestore)
{
    public RemovedAdapterRecord Record { get; } = record;
    public bool CanRestore { get; } = canRestore;
    public string Title => Record.Description is { Length: > 0 } d ? d : Record.Name;
    public string Subtitle => Loc.T("maint.removed.on", Record.RemovedAtUtc.ToLocalTime().ToString("g", Loc.Culture));
}

public sealed class MissingItem(KnownAdapter known)
{
    public KnownAdapter Known { get; } = known;
    public string Title => Known.Description is { Length: > 0 } d ? d : Known.Name;
    public string Subtitle => $"{Known.Name}{(string.IsNullOrEmpty(Known.Mac) ? "" : " · " + Known.Mac)}";
}

public partial class MaintenanceViewModel : ObservableObject
{
    private readonly INetworkService _network;
    private readonly RecoveryStore _store;
    private readonly AppShell _shell;
    private readonly Func<Task> _afterChange;
    private RecoveryData _data;

    public ObservableCollection<RemovableItem> Removable { get; } = [];
    public ObservableCollection<MissingItem> Missing { get; } = [];
    public ObservableCollection<RemovedItem> Removed { get; } = [];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ProtectedNote))] private int _protectedCount;
    [ObservableProperty] private bool _confirming;

    public bool HasRemovable => Removable.Count > 0;
    public bool NoRemovable => Removable.Count == 0;
    public int SelectedCount => Removable.Count(r => r.IsChecked);
    public bool HasSelection => SelectedCount > 0;
    public string SelectedCountText => Loc.T("maint.selectedCount", SelectedCount);
    public string ProtectedNote => Loc.T("maint.protectedNote", ProtectedCount);
    public bool HasProtected => ProtectedCount > 0;

    public bool HasMissing => Missing.Count > 0;
    public bool NoMissing => Missing.Count == 0;
    public string MissingNames => string.Join(", ", Missing.Select(m => m.Title));

    public bool HasRemoved => Removed.Count > 0;
    public bool NoRemoved => Removed.Count == 0;
    public bool CanRecreate => _network.CanCreateVirtualBoxAdapter;
    public bool CannotRecreate => !CanRecreate && Removed.Any(r => RemovalPolicy.IsVirtualBox(r.Record.Service));

    public MaintenanceViewModel(INetworkService network, RecoveryStore store, AppShell shell, Func<Task> afterChange)
    {
        _network = network;
        _store = store;
        _shell = shell;
        _afterChange = afterChange;
        _data = store.Load();
        RebuildRemoved();
    }

    private void RaiseAll()
    {
        foreach (var n in new[] { nameof(HasRemovable), nameof(NoRemovable), nameof(SelectedCount), nameof(HasSelection), nameof(SelectedCountText),
                     nameof(ProtectedNote), nameof(HasProtected), nameof(HasMissing), nameof(NoMissing), nameof(MissingNames),
                     nameof(HasRemoved), nameof(NoRemoved), nameof(CanRecreate), nameof(CannotRecreate) })
            OnPropertyChanged(n);
        DeleteSelectedCommand.NotifyCanExecuteChanged();
    }

    private void RebuildRemoved()
    {
        Removed.Clear();
        foreach (var r in _data.Removed.OrderByDescending(r => r.RemovedAtUtc))
            Removed.Add(new RemovedItem(r, RemovalPolicy.IsVirtualBox(r.Service) && _network.CanCreateVirtualBoxAdapter));
    }

    private void Persist()
    {
        try { _store.Save(_data); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _shell.Error(Loc.T("profiles.writeFail", _store.FilePath, ex.Message)); }
    }

    // ------------------------------------------------------------------ refresh

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IReadOnlyList<AdapterInfo> adapters = [];
        try { adapters = await _network.GetAdaptersAsync(); }
        catch (Exception ex) { _shell.Error(Loc.T("status.failed", Loc.T("conn.reading"), ex.Message)); return; }

        foreach (var item in Removable) item.PropertyChanged -= OnItemChanged;
        Removable.Clear();
        foreach (var a in adapters.Where(a => a.CanRemove).OrderBy(a => a.Description))
        {
            var item = new RemovableItem(a);
            item.PropertyChanged += OnItemChanged;
            Removable.Add(item);
        }
        ProtectedCount = adapters.Count(a => !a.CanRemove);

        // Remember hardware adapters, then see which remembered ones are gone.
        if (RecoveryLogic.UpdateSnapshot(_data, adapters)) Persist();
        Missing.Clear();
        foreach (var k in RecoveryLogic.FindMissing(_data, adapters)) Missing.Add(new MissingItem(k));

        Confirming = false;
        RebuildRemoved();
        RaiseAll();
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RemovableItem.IsChecked)) return;
        Confirming = false;
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedCountText));
        DeleteSelectedCommand.NotifyCanExecuteChanged();
    }

    // ------------------------------------------------------------------ delete virtual adapters

    [RelayCommand]
    private void SelectAll()
    {
        bool all = Removable.All(r => r.IsChecked);
        foreach (var r in Removable) r.IsChecked = !all;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void DeleteSelected() => Confirming = true;

    [RelayCommand]
    private void CancelDelete() => Confirming = false;

    [RelayCommand]
    private async Task ConfirmDeleteAsync()
    {
        var chosen = Removable.Where(r => r.IsChecked).Select(r => r.Adapter).ToList();
        Confirming = false;
        if (chosen.Count == 0) return;

        int removed = 0;
        await _shell.RunAsync(Loc.T("maint.deleting"), async () =>
        {
            var failures = new List<string>();
            foreach (var a in chosen)
            {
                // Save what's needed to recreate the adapter *before* deleting it; drop the record again if deleting fails.
                var record = new RemovedAdapterRecord
                {
                    Name = a.Name, Description = a.Description, PnpId = a.PnpId, Service = a.Service,
                    Mac = a.MacAddress, Ipv4 = a.Ipv4.Clone(),
                };
                _data.Removed.Add(record);
                Persist();
                try { await _network.RemoveAdapterAsync(a.Id); removed++; }
                catch (NetworkOperationException ex)
                {
                    _data.Removed.Remove(record);
                    Persist();
                    failures.Add($"{a.Name}: {ex.Message}");
                }
            }
            if (failures.Count > 0) throw new NetworkOperationException(Loc.T("maint.notRemoved", string.Join(" | ", failures)));
        }, null);

        await RefreshAsync();
        await _afterChange();
        if (removed > 0 && !_shell.IsError) _shell.Info(Loc.T("maint.deleted", removed));
    }

    // ------------------------------------------------------------------ restore

    [RelayCommand]
    private async Task RescanAsync()
    {
        if (await _shell.RunAsync(Loc.T("maint.hw.rescanning"), () => _network.RescanHardwareAsync(), Loc.T("maint.hw.rescanned")))
        {
            await Task.Delay(1500); // let Windows finish installing the drivers it just found
            await RefreshAsync();
            await _afterChange();
        }
    }

    [RelayCommand]
    private void ForgetMissing(MissingItem? item)
    {
        if (item is null) return;
        _data.Hardware.Remove(item.Known);
        Persist();
        Missing.Remove(item);
        RaiseAll();
    }

    [RelayCommand]
    private async Task RecreateAsync(RemovedItem? item)
    {
        if (item is null) return;
        var rec = item.Record;
        IReadOnlyList<AdapterInfo> before = [];
        string? created = null;

        var ok = await _shell.RunAsync(Loc.T("maint.removed.restoring", item.Title), async () =>
        {
            before = await _network.GetAdaptersAsync();
            await _network.CreateVirtualBoxAdapterAsync();
            await Task.Delay(1500);
            var after = await _network.GetAdaptersAsync();
            // The new adapter is whichever one wasn't there before; give it the IPv4 settings the old one had.
            var fresh = after.FirstOrDefault(a => before.All(b => b.Id != a.Id));
            if (fresh is not null)
            {
                created = fresh.Description;
                if (rec.Ipv4.Method == Ipv4Method.Manual && fresh.InterfaceIndex > 0)
                    await _network.ApplyIpv4Async(fresh.InterfaceIndex, rec.Ipv4);
            }
        }, null);
        if (!ok) return;

        _data.Removed.Remove(rec);
        Persist();
        await RefreshAsync();
        await _afterChange();
        _shell.Info(Loc.T("maint.removed.restored", created ?? item.Title));
    }

    [RelayCommand]
    private void ForgetRemoved(RemovedItem? item)
    {
        if (item is null) return;
        _data.Removed.Remove(item.Record);
        Persist();
        Removed.Remove(item);
        RaiseAll();
    }
}
