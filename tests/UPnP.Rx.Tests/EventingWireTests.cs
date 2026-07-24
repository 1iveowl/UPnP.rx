using UPnP.Rx.Eventing;
using Xunit;

namespace UPnP.Rx.Tests;

public class GenaParserTests
{
    [Fact]
    public void ParsesAStandardPropertySet()
    {
        const string xml = """
            <?xml version="1.0"?>
            <e:propertyset xmlns:e="urn:schemas-upnp-org:event-1-0">
              <e:property><SystemUpdateID>27</SystemUpdateID></e:property>
              <e:property><ContainerUpdateIDs>0,27</ContainerUpdateIDs></e:property>
            </e:propertyset>
            """;

        var result = GenaParser.ParsePropertySet(xml);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(2, result.Value.Count);
        Assert.Equal(new EventedProperty("SystemUpdateID", "27"), result.Value[0]);
        Assert.Equal(new EventedProperty("ContainerUpdateIDs", "0,27"), result.Value[1]);
    }

    [Fact]
    public void EscapedLastChangePayload_ArrivesDecodedButUntyped()
    {
        // Sonos-style AVTransport event: the value is a complete escaped XML
        // document. It must pass through as a decoded string (typed parsing is
        // a 4.1 candidate).
        const string xml = """
            <e:propertyset xmlns:e="urn:schemas-upnp-org:event-1-0">
              <e:property>
                <LastChange>&lt;Event xmlns="urn:schemas-upnp-org:metadata-1-0/AVT/"&gt;&lt;InstanceID val="0"&gt;&lt;TransportState val="PLAYING"/&gt;&lt;/InstanceID&gt;&lt;/Event&gt;</LastChange>
              </e:property>
            </e:propertyset>
            """;

        var result = GenaParser.ParsePropertySet(xml);

        Assert.True(result.IsSuccess, result.Error);
        var lastChange = Assert.Single(result.Value);
        Assert.Equal("LastChange", lastChange.Name);
        Assert.StartsWith("<Event", lastChange.Value);
        Assert.Contains("TransportState val=\"PLAYING\"", lastChange.Value);
    }

    [Fact]
    public void MissingNamespaceAndCasing_AreTolerated()
    {
        const string xml = "<PROPERTYSET><PROPERTY><Status>OK</Status></PROPERTY></PROPERTYSET>";

        var result = GenaParser.ParsePropertySet(xml);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(new EventedProperty("Status", "OK"), Assert.Single(result.Value));
    }

    [Fact]
    public void BareAmpersand_IsRepaired()
    {
        const string xml = """
            <e:propertyset xmlns:e="urn:schemas-upnp-org:event-1-0">
              <e:property><CurrentTrackTitle>Tom & Jerry</CurrentTrackTitle></e:property>
            </e:propertyset>
            """;

        var result = GenaParser.ParsePropertySet(xml);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal("Tom & Jerry", result.Value[0].Value);
    }

    [Fact]
    public void EmptyPropertySet_IsAValidKeepAlive()
    {
        var result = GenaParser.ParsePropertySet(
            "<e:propertyset xmlns:e=\"urn:schemas-upnp-org:event-1-0\"/>");

        Assert.True(result.IsSuccess, result.Error);
        Assert.Empty(result.Value);
    }

    [Fact]
    public void GarbageAndNonPropertySetBodies_Fail()
    {
        Assert.False(GenaParser.ParsePropertySet("not xml").IsSuccess);
        Assert.False(GenaParser.ParsePropertySet("<something-else/>").IsSuccess);
    }
}

public class GenaHeadersTests
{
    [Theory]
    [InlineData("Second-1800", 1800)]
    [InlineData("second-300", 300)]
    [InlineData("  SECOND-60  ", 60)]
    [InlineData("900", 900)]                       // bare number: seen in the wild
    public void ParseTimeout_IsLenient(string value, int expectedSeconds) =>
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), GenaHeaders.ParseTimeout(value));

    [Theory]
    [InlineData("Second-infinite")]
    [InlineData("infinite")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Second--5")]
    [InlineData("garbage")]
    public void ParseTimeout_UnusableValuesYieldNull(string? value) =>
        Assert.Null(GenaHeaders.ParseTimeout(value));

    [Fact]
    public void ComposeTimeout_RoundTrips()
    {
        Assert.Equal("Second-1800", GenaHeaders.ComposeTimeout(TimeSpan.FromMinutes(30)));
        Assert.Equal("Second-infinite", GenaHeaders.ComposeTimeout(null));
        Assert.Equal(TimeSpan.FromMinutes(30), GenaHeaders.ParseTimeout(GenaHeaders.ComposeTimeout(TimeSpan.FromMinutes(30))));
    }

    [Fact]
    public void ComposeCallback_WrapsInAngleBrackets() =>
        Assert.Equal("<http://192.168.1.42:49500/upnp/events/abc>",
            GenaHeaders.ComposeCallback(new Uri("http://192.168.1.42:49500/upnp/events/abc")));

    [Theory]
    [InlineData("0", 0u)]
    [InlineData(" 42 ", 42u)]
    public void ParseSeq_ParsesNumbers(string value, uint expected) =>
        Assert.Equal(expected, GenaHeaders.ParseSeq(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("abc")]
    public void ParseSeq_UnusableValuesYieldNull(string? value) =>
        Assert.Null(GenaHeaders.ParseSeq(value));
}
