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
    Task SetAdapterEnabledAsync(int interfaceIndex, bool enabled, CancellationToken ct = default);
    Task RenewDhcpLeaseAsync(int interfaceIndex, CancellationToken ct = default);
    Task FlushDnsCacheAsync(CancellationToken ct = default);

    Task<IReadOnlyList<RouteEntry>> GetRoutesAsync(CancellationToken ct = default);
    Task AddRouteAsync(RouteRequest route, CancellationToken ct = default);
    Task DeleteRouteAsync(RouteEntry route, CancellationToken ct = default);
}

public sealed class NetworkOperationException(string message, Exception? inner = null) : Exception(message, inner);
