using UPnP.Rx.Model;
using UPnP.Rx.Parsing;
using Xunit;

namespace UPnP.Rx.Tests;

public class ScpdExtensionsTests
{
    private static readonly Scpd _scpd = ScpdParser.ParseScpd(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "wanipconnection1_scpd.xml"))).Value!;

    private static Dictionary<string, string> ValidAddArguments() => new()
    {
        ["NewRemoteHost"] = "",
        ["NewExternalPort"] = "18080",
        ["NewProtocol"] = "TCP",
        ["NewInternalPort"] = "18080",
        ["NewInternalClient"] = "192.168.1.42",
        ["NewEnabled"] = "1",
        ["NewPortMappingDescription"] = "test",
        ["NewLeaseDuration"] = "3600"
    };

    [Fact]
    public void ValidArguments_AreReturnedInScpdOrder()
    {
        var result = _scpd.ValidateAndOrderArguments("AddPortMapping", ValidAddArguments());

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(
            ["NewRemoteHost", "NewExternalPort", "NewProtocol", "NewInternalPort",
             "NewInternalClient", "NewEnabled", "NewPortMappingDescription", "NewLeaseDuration"],
            result.Value.Keys);
    }

    [Fact]
    public void MissingInArgument_Fails()
    {
        var arguments = ValidAddArguments();
        arguments.Remove("NewProtocol");

        var result = _scpd.ValidateAndOrderArguments("AddPortMapping", arguments);

        Assert.False(result.IsSuccess);
        Assert.Contains("NewProtocol", result.Error);
    }

    [Fact]
    public void UnknownArgument_Fails()
    {
        var arguments = ValidAddArguments();
        arguments["NewBogus"] = "x";

        var result = _scpd.ValidateAndOrderArguments("AddPortMapping", arguments);

        Assert.False(result.IsSuccess);
        Assert.Contains("NewBogus", result.Error);
    }

    [Fact]
    public void AllowedValueListViolation_Fails()
    {
        var arguments = ValidAddArguments();
        arguments["NewProtocol"] = "SCTP";                      // list is TCP|UDP

        var result = _scpd.ValidateAndOrderArguments("AddPortMapping", arguments);

        Assert.False(result.IsSuccess);
        Assert.Contains("allowed value list", result.Error);
    }

    [Fact]
    public void AllowedRangeViolation_Fails()
    {
        var arguments = ValidAddArguments();
        arguments["NewLeaseDuration"] = "700000";               // range max 604800

        var result = _scpd.ValidateAndOrderArguments("AddPortMapping", arguments);

        Assert.False(result.IsSuccess);
        Assert.Contains("maximum", result.Error);
    }

    [Fact]
    public void DataTypeViolation_Fails()
    {
        var arguments = ValidAddArguments();
        arguments["NewExternalPort"] = "not-a-port";            // ui2

        var result = _scpd.ValidateAndOrderArguments("AddPortMapping", arguments);

        Assert.False(result.IsSuccess);
        Assert.Contains("ui2", result.Error);
    }

    [Fact]
    public void UnknownAction_Fails()
    {
        var result = _scpd.ValidateAndOrderArguments("NoSuchAction");

        Assert.False(result.IsSuccess);
        Assert.Contains("NoSuchAction", result.Error);
    }

    [Fact]
    public void ActionWithoutInArguments_SucceedsEmpty()
    {
        var result = _scpd.ValidateAndOrderArguments("GetExternalIPAddress");

        Assert.True(result.IsSuccess, result.Error);
        Assert.Empty(result.Value);
    }
}
