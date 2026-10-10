using CommunityToolkit.Mvvm.ComponentModel;

namespace NetManager.App.ViewModels;

/// <summary>One editable row of the address table: address + netmask (or prefix length).</summary>
public partial class AddressRow(string address = "", string mask = "255.255.255.0") : ObservableObject
{
    [ObservableProperty] private string _address = address;
    [ObservableProperty] private string _mask = mask;
}

/// <summary>One editable default gateway: address plus either "automatic metric" or an explicit one.</summary>
public partial class GatewayRow(string address = "", int? metric = null) : ObservableObject
{
    [ObservableProperty] private string _address = address;
    [ObservableProperty] private bool _isAutomatic = metric is null;
    [ObservableProperty] private string _metricText = metric?.ToString() ?? "";
}
