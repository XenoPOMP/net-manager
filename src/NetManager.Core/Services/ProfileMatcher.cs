using NetManager.Core.Models;

namespace NetManager.Core.Services;

/// <summary>Decides whether a saved profile is what the adapter is configured with right now.</summary>
public static class ProfileMatcher
{
    public static bool IsActive(ConnectionProfile profile, IEnumerable<AdapterInfo> adapters, IEnumerable<RouteEntry> routes)
    {
        var adapter = ProfileStore.FindAdapter(profile, adapters);
        if (adapter is null || !adapter.IsEnabled) return false;
        return Ipv4Matches(profile.Ipv4, adapter.Ipv4) && RoutesMatch(profile, adapter, routes);
    }

    public static bool Ipv4Matches(Ipv4Config want, Ipv4Config have)
    {
        if (want.Method != have.Method) return false;
        if (want.InterfaceMetric != have.InterfaceMetric) return false;
        if (want.DnsAutomatic != have.DnsAutomatic) return false;
        if (!want.DnsAutomatic && !want.DnsServers.SequenceEqual(have.DnsServers)) return false;   // the order is the preference order

        // With DHCP the addresses and gateways are whatever the lease handed out, so they say nothing about the profile.
        if (want.Method == Ipv4Method.Manual)
        {
            if (!want.Addresses.ToHashSet().SetEquals(have.Addresses)) return false;
            if (!want.Gateways.Select(g => g.Address).ToHashSet().SetEquals(have.Gateways.Select(g => g.Address))) return false;
            foreach (var g in want.Gateways.Where(g => g.Metric is not null))
                if (have.Gateways.First(h => h.Address == g.Address).Metric != g.Metric) return false;
            // An "automatic" gateway metric in the profile accepts whatever Windows stored (it may have had to store 1).
        }
        return true;
    }

    /// <summary>The adapter's static routes (default routes aside — those are the gateways) must be exactly the profile's routes.</summary>
    public static bool RoutesMatch(ConnectionProfile profile, AdapterInfo adapter, IEnumerable<RouteEntry> routes)
    {
        var have = routes.Where(r => r.Kind == RouteKind.Static && r.InterfaceIndex == adapter.InterfaceIndex && !r.IsDefault)
            .Select(r => (r.Destination, r.PrefixLength, r.NextHop)).ToHashSet();
        var want = profile.Routes.Where(r => !r.IsDefaultRoute)
            .Select(r => (r.Destination, r.PrefixLength, r.NextHop)).ToHashSet();
        return have.SetEquals(want);
    }
}
