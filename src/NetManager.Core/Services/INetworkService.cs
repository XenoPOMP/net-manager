using NetManager.Core.Models;

namespace NetManager.Core.Services;

/// <summary>Everything the UI needs from the operating system. Implemented natively for Windows and mocked elsewhere.</summary>
public interface INetworkService
{
    /// <summary>Short description of the backend, shown in the status bar.</summary>
    string BackendName { get; }

    /// <summary>False when the process lacks the rights to change settings (the UI then warns the user).</summary>
    bool CanModify { get; }

    Task<IReadOnlyList<AdapterInfo>> GetAdaptersAsync(CancellationToken ct = default);
    Task ApplyIpv4Async(int interfaceIndex, Ipv4Config config, CancellationToken ct = default);
    Task SetAdapterEnabledAsync(string adapterId, bool enabled, CancellationToken ct = default);
    Task RenewDhcpLeaseAsync(int interfaceIndex, CancellationToken ct = default);
    Task FlushDnsCacheAsync(CancellationToken ct = default);

    /// <summary>
    /// Uninstalls a virtual adapter. Implementations must refuse (throw <see cref="NetworkOperationException"/>) anything
    /// <see cref="RemovalPolicy"/> does not allow, whatever the caller asked for.
    /// </summary>
    Task RemoveAdapterAsync(string adapterId, CancellationToken ct = default);

    /// <summary>Asks Windows to re-detect hardware; brings back physical adapters whose device was uninstalled.</summary>
    Task RescanHardwareAsync(CancellationToken ct = default);

    /// <summary>True when VirtualBox is installed so removed host-only adapters can be recreated.</summary>
    bool CanCreateVirtualBoxAdapter { get; }
    Task CreateVirtualBoxAdapterAsync(CancellationToken ct = default);

    Task<IReadOnlyList<RouteEntry>> GetRoutesAsync(CancellationToken ct = default);
    Task AddRouteAsync(RouteRequest route, CancellationToken ct = default);
    Task DeleteRouteAsync(RouteEntry route, CancellationToken ct = default);
}

/// <summary>Machine-readable failure reasons, so callers never have to match on (localized) message text.</summary>
public enum NetworkErrorKind { General, AlreadyExists }

public sealed class NetworkOperationException(string message, Exception? inner = null, NetworkErrorKind kind = NetworkErrorKind.General)
    : Exception(message, inner)
{
    public NetworkErrorKind Kind { get; } = kind;
}
