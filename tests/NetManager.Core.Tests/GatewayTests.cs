using System.Text.Json;
using NetManager.Core.Models;
using NetManager.Core.Services;

namespace NetManager.Core.Tests;

public class GatewayTests
{
    private static GatewayEntry G(string address, int? metric = null) => new(address, metric);

    private static Ipv4Config Manual(GatewayEntry first, params GatewayEntry[] more) => ManualList([first, .. more]);

    private static Ipv4Config ManualList(IEnumerable<GatewayEntry> gateways) => new()
    {
        Method = Ipv4Method.Manual,
        Addresses = [new("10.144.40.122", 24), new("192.168.25.252", 24)],
        Gateways = [.. gateways],
    };

    [Fact]
    public void SeveralGatewaysWithMixedMetricsAreValid() =>
        Assert.Empty(Ipv4Util.Validate(Manual(G("10.144.40.1"), G("192.168.25.1", 20))));   // each is in one of the two subnets

    [Fact]
    public void GatewayMustBeInsideSomeConfiguredSubnet() =>
        Assert.Single(Ipv4Util.Validate(Manual(G("10.144.40.1"), G("172.16.0.1"))));

    [Theory]
    [InlineData(0)]
    [InlineData(10000)]
    [InlineData(-5)]
    public void ExplicitMetricMustBeBetween1And9999(int metric) =>
        Assert.NotEmpty(Ipv4Util.Validate(Manual(G("10.144.40.1", metric))));

    [Theory]
    [InlineData(1)]
    [InlineData(9999)]
    public void MetricBoundsAreInclusive(int metric) =>
        Assert.Empty(Ipv4Util.Validate(Manual(G("10.144.40.1", metric))));

    [Fact]
    public void DuplicateGatewayIsRejected() =>
        Assert.Single(Ipv4Util.Validate(Manual(G("10.144.40.1"), G("10.144.40.1", 5))));

    [Fact]
    public void AtMostFiveGateways()
    {
        var six = Enumerable.Range(1, 6).Select(i => new GatewayEntry($"10.144.40.{i}")).ToArray();
        Assert.NotEmpty(Ipv4Util.Validate(ManualList(six)));
        Assert.Empty(Ipv4Util.Validate(ManualList(six.Take(5))));
    }

    [Fact]
    public void InterfaceMetricIsValidatedInBothMethods()
    {
        Assert.NotEmpty(Ipv4Util.Validate(new Ipv4Config { Method = Ipv4Method.Automatic, InterfaceMetric = 0 }));
        Assert.Empty(Ipv4Util.Validate(new Ipv4Config { Method = Ipv4Method.Automatic, InterfaceMetric = 25 }));
    }

    [Fact]
    public void GatewaysAreIgnoredForDhcp() =>
        Assert.Empty(Ipv4Util.Validate(new Ipv4Config { Method = Ipv4Method.Automatic, Gateways = [new("not-an-ip")] }));

    [Fact]
    public void OldProfilesWithASingleGatewayStillLoad()
    {
        // Format written before several gateways were supported.
        const string old = """{ "Method": "Manual", "Addresses": [ { "Address": "10.0.0.5", "PrefixLength": 24 } ], "Gateway": "10.0.0.1", "DnsAutomatic": true }""";
        var cfg = JsonSerializer.Deserialize<Ipv4Config>(old, new JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })!;
        var g = Assert.Single(cfg.Gateways);
        Assert.Equal("10.0.0.1", g.Address);
        Assert.Null(g.Metric);          // old files meant "automatic"
    }

    [Fact]
    public void SavedFilesUseTheNewFormatOnly()
    {
        var json = JsonSerializer.Serialize(Manual(G("10.144.40.1", 5)));
        Assert.DoesNotContain("\"Gateway\"", json);
        Assert.Contains("\"Gateways\"", json);
    }

    [Fact]
    public void ProfileKeepsGatewayOrderMetricsAndInterfaceMetric()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var store = new ProfileStore(path);
            var cfg = Manual(G("10.144.40.1", 5), G("192.168.25.1"));
            cfg.InterfaceMetric = 20;
            store.Save([new ConnectionProfile { Name = "x", AdapterName = "Ethernet", Ipv4 = cfg }]);

            var back = Assert.Single(store.Load()).Ipv4;
            Assert.Equal([new GatewayEntry("10.144.40.1", 5), new GatewayEntry("192.168.25.1")], back.Gateways);
            Assert.Equal(20, back.InterfaceMetric);
            Assert.Equal("10.144.40.1", back.PrimaryGateway);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CloneIsIndependent()
    {
        var a = Manual(G("10.144.40.1", 5));
        var b = a.Clone();
        b.Gateways.Add(new("192.168.25.1"));
        Assert.Single(a.Gateways);
    }
}
