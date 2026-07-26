using UPnP.Rx.Model;
using UPnP.Rx.Parsing;
using Xunit;
using static UPnP.Rx.Tests.TestHelpers.TestKit;

namespace UPnP.Rx.Tests;

public class ScpdParserTests
{
    private static Scpd ParseFixture(string name)
    {
        var result = ScpdParser.ParseScpd(Fixture(name));

        Assert.True(result.IsSuccess, result.Error);
        return result.Value;
    }

    // ---- WANIPConnection:1 (subset of the standardized service template; the
    //      document devices serve is essentially this, verbatim) ----

    [Fact]
    public void WanIpConnection_ParsesActions()
    {
        var scpd = ParseFixture("wanipconnection1_scpd.xml");

        Assert.Equal(new SpecVersion { Major = 1, Minor = 0 }, scpd.SpecVersion);
        Assert.Equal(5, scpd.Actions.Count);
        Assert.Equal(
            ["GetStatusInfo", "GetGenericPortMappingEntry", "AddPortMapping", "DeletePortMapping", "GetExternalIPAddress"],
            scpd.Actions.Select(a => a.Name));
    }

    [Fact]
    public void WanIpConnection_ParsesArgumentsWithDirections()
    {
        var scpd = ParseFixture("wanipconnection1_scpd.xml");

        var addPortMapping = scpd.Actions.Single(a => a.Name == "AddPortMapping");
        Assert.Equal(8, addPortMapping.Arguments.Count);
        Assert.All(addPortMapping.Arguments, a => Assert.Equal(ArgumentDirection.In, a.Direction));

        var getExternalIp = scpd.Actions.Single(a => a.Name == "GetExternalIPAddress");
        var outArg = Assert.Single(getExternalIp.Arguments);
        Assert.Equal("NewExternalIPAddress", outArg.Name);
        Assert.Equal(ArgumentDirection.Out, outArg.Direction);
        Assert.Equal("ExternalIPAddress", outArg.RelatedStateVariable);
    }

    [Fact]
    public void WanIpConnection_ParsesStateVariables()
    {
        var scpd = ParseFixture("wanipconnection1_scpd.xml");

        Assert.Equal(13, scpd.StateVariables.Count);

        var protocol = scpd.StateVariables.Single(v => v.Name == "PortMappingProtocol");
        Assert.Equal("string", protocol.DataType);
        Assert.False(protocol.SendsEvents);
        Assert.Equal(["TCP", "UDP"], protocol.AllowedValues);

        var lease = scpd.StateVariables.Single(v => v.Name == "PortMappingLeaseDuration");
        Assert.Equal("ui4", lease.DataType);
        Assert.Equal("0", lease.DefaultValue);
        Assert.Equal("0", lease.AllowedRange?.Minimum);
        Assert.Equal("604800", lease.AllowedRange?.Maximum);
        Assert.Null(lease.AllowedRange?.Step);

        var entries = scpd.StateVariables.Single(v => v.Name == "PortMappingNumberOfEntries");
        Assert.True(entries.SendsEvents);
    }

    // ---- Real-world sloppiness: no namespace, bare '&' in an action name,
    //      uppercase direction, retval, whitespace inside relatedStateVariable,
    //      empty allowedValueList, missing sendEvents (defaults to yes) ----

    [Fact]
    public void QuirkyScpd_ParsesLeniently()
    {
        var scpd = ParseFixture("quirky_scpd.xml");

        Assert.Null(scpd.SpecVersion);
        Assert.Equal(2, scpd.Actions.Count);

        var getTarget = scpd.Actions[0];
        Assert.Equal("GetTarget&Status", getTarget.Name);        // repaired '&', token-normalized
        var retval = Assert.Single(getTarget.Arguments);
        Assert.True(retval.IsReturnValue);
        Assert.Equal(ArgumentDirection.Unknown, retval.Direction); // direction missing → kept, not dropped

        var setTarget = scpd.Actions[1];
        var inArg = Assert.Single(setTarget.Arguments);
        Assert.Equal(ArgumentDirection.In, inArg.Direction);       // "IN" — case-tolerant
        Assert.Equal("Target", inArg.RelatedStateVariable);        // embedded newline stripped

        var target = Assert.Single(scpd.StateVariables);
        Assert.True(target.SendsEvents);                           // attribute absent → yes per UDA
        Assert.Empty(target.AllowedValues);                        // empty allowedValueList → []
    }

    // ---- Failure only when the input is not XML at all ----

    [Fact]
    public void NotXml_Fails()
    {
        var result = ScpdParser.ParseScpd("not xml");

        Assert.False(result.IsSuccess);
        Assert.Contains("not well-formed", result.Error);
    }

    [Fact]
    public void EmptyScpdRoot_SucceedsWithEmptyLists()
    {
        var result = ScpdParser.ParseScpd("<scpd xmlns=\"urn:schemas-upnp-org:service-1-0\"/>");

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value.Actions);
        Assert.Empty(result.Value.StateVariables);
    }
}
