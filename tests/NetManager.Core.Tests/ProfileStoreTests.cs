using NetManager.Core.Models;
using NetManager.Core.Services;

namespace NetManager.Core.Tests;

public class ProfileStoreTests
{
    [Fact]
    public void RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var store = new ProfileStore(path);
            Assert.Empty(store.Load());

            store.Save([new ConnectionProfile
            {
                Name = "Office",
                AdapterName = "Ethernet",
                Ipv4 = new Ipv4Config { Method = Ipv4Method.Manual, Addresses = [new("10.0.0.5", 24)], Gateways = [new("10.0.0.1", 5), new("10.0.0.254")], InterfaceMetric = 20 },
                Routes = [new ProfileRoute { Destination = "172.16.0.0", PrefixLength = 12, NextHop = "10.0.0.254" }],
            }]);

            var loaded = Assert.Single(store.Load());
            Assert.Equal("Office", loaded.Name);
            Assert.Equal(Ipv4Method.Manual, loaded.Ipv4.Method);
            Assert.Equal("10.0.0.5/24", loaded.Ipv4.Addresses[0].ToString());
            Assert.Equal("10.0.0.254", loaded.Routes[0].NextHop);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CorruptFileYieldsEmptyList()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        File.WriteAllText(path, "{ not json");
        try { Assert.Empty(new ProfileStore(path).Load()); }
        finally { File.Delete(path); }
    }
}
