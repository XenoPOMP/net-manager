namespace NetManager.Core.Models;

/// <summary>What Windows tells us about an adapter's device (Win32_NetworkAdapter): the inputs of <see cref="RemovalPolicy"/>.</summary>
public sealed record AdapterFacts(string? PnpId, string? Service, string? Manufacturer, string? Description, bool? Physical);

/// <param name="ReasonKey">Localization key explaining the decision (shown to the user).</param>
public sealed record RemovalDecision(bool Allowed, string ReasonKey)
{
    public static RemovalDecision Unknown { get; } = new(false, "removal.unknown");
}

/// <summary>
/// Decides which adapters may be uninstalled by this app. Deliberately an allow-list: an adapter is removable only if it is
/// positively identified as one created by a known virtualization driver. Hardware always wins, and anything unrecognised is protected.
/// The Windows backend re-evaluates this immediately before removing, so a UI bug can never delete a hardware adapter.
/// </summary>
public static class RemovalPolicy
{
    /// <summary>Driver services of virtual adapters that can be recreated by their owning program.</summary>
    private static readonly string[] RemovableServices =
        ["VBoxNetAdp", "VBoxNetAdp6", "VMnetAdapter"];

    /// <summary>Hyper-V / WSL virtual switches: owned by the host networking service, removing them breaks WSL and VMs.</summary>
    private static readonly string[] HyperVServices = ["VMSMP", "vmswitch"];

    /// <summary>Device-instance prefixes of real (bus-attached) hardware.</summary>
    private static readonly string[] HardwareEnumerators =
        [@"PCI\", @"USB\", @"USB4\", @"ACPI\", @"BTH\", @"BTHENUM\", @"BTHPORT\", @"SD\", @"PCMCIA\", @"SCSI\", @"1394\", @"VMBUS\", @"THUNDERBOLT\"];

    public static bool IsHardware(AdapterFacts f) =>
        f.Physical == true || (f.PnpId is { } id && HardwareEnumerators.Any(p => id.StartsWith(p, StringComparison.OrdinalIgnoreCase)));

    public static RemovalDecision Evaluate(AdapterFacts f)
    {
        if (IsHardware(f)) return new(false, "removal.hardware");

        bool hyperV = (f.Service is { } s0 && HyperVServices.Contains(s0, StringComparer.OrdinalIgnoreCase))
            || (f.PnpId?.StartsWith(@"ROOT\VMS_", StringComparison.OrdinalIgnoreCase) ?? false)
            || (f.Description?.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) ?? false);
        if (hyperV) return new(false, "removal.hyperv");

        if (f.Service is { } s && RemovableServices.Contains(s, StringComparer.OrdinalIgnoreCase))
            return new(true, "removal.ok");

        if (f.Manufacturer?.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) ?? false)
            return new(false, "removal.system");

        return RemovalDecision.Unknown;
    }

    public static bool IsVirtualBox(string? service) =>
        service is not null && service.StartsWith("VBoxNetAdp", StringComparison.OrdinalIgnoreCase);
}
