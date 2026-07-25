using System.Reactive.Linq;
using System.Reactive.Subjects;
using UPnP.Rx.Eventing;
using UPnP.Rx.Eventing.Av;
using Xunit;

namespace UPnP.Rx.Tests;

public class LastChangeTests
{
    private const string AvTransportPayload =
        "<Event xmlns=\"urn:schemas-upnp-org:metadata-1-0/AVT/\">" +
        "<InstanceID val=\"0\">" +
        "<TransportState val=\"PLAYING\"/>" +
        "<CurrentTrackURI val=\"http://192.168.0.10/track.mp3\"/>" +
        "</InstanceID></Event>";

    private const string RenderingControlPayload =
        "<Event xmlns=\"urn:schemas-upnp-org:metadata-1-0/RCS/\">" +
        "<InstanceID val=\"0\">" +
        "<Volume channel=\"Master\" val=\"25\"/>" +
        "<Volume channel=\"LF\" val=\"100\"/>" +
        "<Mute channel=\"Master\" val=\"0\"/>" +
        "</InstanceID></Event>";

    [Fact]
    public void AvTransport_Payload_Parses()
    {
        var result = LastChangeParser.Parse(AvTransportPayload);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.Count);
        Assert.Equal(new AvPropertyChange(0, "TransportState", "PLAYING", null), result.Value[0]);
    }

    [Fact]
    public void RenderingControl_Channels_Surface()
    {
        var result = LastChangeParser.Parse(RenderingControlPayload);

        Assert.True(result.IsSuccess);
        Assert.Equal(new AvPropertyChange(0, "Volume", "25", "Master"), result.Value[0]);
        Assert.Equal(new AvPropertyChange(0, "Volume", "100", "LF"), result.Value[1]);
        Assert.Equal(new AvPropertyChange(0, "Mute", "0", "Master"), result.Value[2]);
    }

    [Fact]
    public void MultipleInstances_AndCasing_AreTolerated()
    {
        var result = LastChangeParser.Parse(
            "<event><instanceid VAL=\"1\"><TransportState VAL=\"PAUSED_PLAYBACK\"/></instanceid>" +
            "<InstanceID val=\"2\"><Volume Channel=\"Master\" val=\"7\"/></InstanceID></event>");

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value[0].InstanceId);
        Assert.Equal(2, result.Value[1].InstanceId);
        Assert.Equal("Master", result.Value[1].Channel);
    }

    [Fact]
    public void MissingInstanceWrapper_AssumesInstanceZero()
    {
        var result = LastChangeParser.Parse("<Event><TransportState val=\"STOPPED\"/></Event>");

        Assert.True(result.IsSuccess);
        Assert.Equal(new AvPropertyChange(0, "TransportState", "STOPPED", null), Assert.Single(result.Value));
    }

    [Fact]
    public void BareAmpersand_IsRepaired()
    {
        var result = LastChangeParser.Parse(
            "<Event><InstanceID val=\"0\"><CurrentTrackMetaData val=\"Tom & Jerry\"/></InstanceID></Event>");

        Assert.True(result.IsSuccess);
        Assert.Equal("Tom & Jerry", Assert.Single(result.Value).Value);
    }

    [Fact]
    public void ElementText_IsTheFallback_WhenValIsAbsent()
    {
        var result = LastChangeParser.Parse(
            "<Event><InstanceID val=\"0\"><TransportState>PLAYING</TransportState></InstanceID></Event>");

        Assert.True(result.IsSuccess);
        Assert.Equal("PLAYING", Assert.Single(result.Value).Value);
    }

    [Fact]
    public void Garbage_Fails_EmptyEventSucceedsEmpty()
    {
        Assert.False(LastChangeParser.Parse("not xml at all").IsSuccess);
        var empty = LastChangeParser.Parse("<Event/>");
        Assert.True(empty.IsSuccess);
        Assert.Empty(empty.Value);
    }

    [Fact]
    public void SelectAvChanges_FlattensLastChange_AndIgnoresTheRest()
    {
        using var events = new Subject<UpnpEvent>();
        var received = new List<AvPropertyChange>();
        using var subscription = events.SelectAvChanges().Subscribe(received.Add);

        events.OnNext(new Subscribed("uuid:s", TimeSpan.FromMinutes(30)));
        events.OnNext(new PropertyChange("LastChange", RenderingControlPayload, 0, true, false));
        events.OnNext(new PropertyChange("SomethingElse", "x", 1, false, false));
        events.OnNext(new PropertyChange("LastChange", "garbage", 2, false, false));

        Assert.Equal(3, received.Count);                 // the RCS payload only
        Assert.Equal("Volume", received[0].Name);
    }
}
