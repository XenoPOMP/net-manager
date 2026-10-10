using CommunityToolkit.Mvvm.ComponentModel;

namespace NetManager.App.ViewModels;

/// <summary>One editable row of the address table: address + netmask (or prefix length).</summary>
public partial class AddressRow(string address = "", string mask = "255.255.255.0") : ObservableObject
{
    [ObservableProperty] private string _address = address;
    [ObservableProperty] private string _mask = mask;
}
