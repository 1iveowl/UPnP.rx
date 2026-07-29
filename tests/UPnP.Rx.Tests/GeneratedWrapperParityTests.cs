using System.Net;
using System.Net.Http;
using System.Text;
using UPnP.Rx.PortMapping;
using UPnP.Rx.Tests.TestHelpers;
using Xunit;

namespace UPnP.Rx.Tests;

/// <summary>
/// The adoption gate for the SCPD generator (6.0.0 plan §8b): the generated
/// <see cref="WanIpConnection"/> must compose <em>byte-identical</em> SOAP - envelope and
/// SOAPACTION header alike - to what the hand-written <see cref="InternetGateway"/> sends.
/// </summary>
/// <remarks>
/// Process rule 8: any change to composed output is held byte-identical rather than
/// eyeballed. Until this is green, <see cref="InternetGateway"/> does not adopt the
/// generated wrapper - the decision is a measurement, not momentum.
/// </remarks>
public class GeneratedWrapperParityTests
{
    private const string Location = "http://192.168.1.1:49152/desc.xml";
    private const string ControlUrl = "http://192.168.1.1:49152/upnp/control/WANPPPConn1";

    [Fact]
    public async Task AddPortMapping_ComposesTheSameEnvelopeAndAction()
    {
        var (hand, generated) = await CaptureAsync(
            gateway => gateway.AddPortMappingAsync(
                18080, 18081, Protocol.Tcp, "test map", TimeSpan.FromHours(1),
                IPAddress.Parse("192.168.1.42"), TestContext.Current.CancellationToken),
            wrapper => wrapper.AddPortMappingAsync(
                string.Empty, 18080, "TCP", 18081, "192.168.1.42", true, "test map", 3600,
                TestContext.Current.CancellationToken),
            "AddPortMapping");

        AssertIdentical(hand, generated);
    }

    [Fact]
    public async Task DeletePortMapping_ComposesTheSameEnvelopeAndAction()
    {
        var (hand, generated) = await CaptureAsync(
            gateway => gateway.DeletePortMappingAsync(18080, Protocol.Tcp, TestContext.Current.CancellationToken),
            wrapper => wrapper.DeletePortMappingAsync(
                string.Empty, 18080, "TCP", TestContext.Current.CancellationToken),
            "DeletePortMapping");

        AssertIdentical(hand, generated);
    }

    [Fact]
    public async Task GetExternalIPAddress_ComposesTheSameEnvelopeAndAction()
    {
        var (hand, generated) = await CaptureAsync(
            gateway => gateway.GetExternalIPAddressAsync(TestContext.Current.CancellationToken),
            wrapper => wrapper.GetExternalIPAddressAsync(TestContext.Current.CancellationToken),
            "GetExternalIPAddress");

        AssertIdentical(hand, generated);
    }

    [Fact]
    public async Task GetStatusInfo_ComposesTheSameEnvelopeAndAction()
    {
        var (hand, generated) = await CaptureAsync(
            gateway => gateway.GetStatusInfoAsync(TestContext.Current.CancellationToken),
            wrapper => wrapper.GetStatusInfoAsync(TestContext.Current.CancellationToken),
            "GetStatusInfo");

        AssertIdentical(hand, generated);
    }

    [Fact]
    public void TheCheckedInDocumentDoesNotCoverEveryActionInternetGatewayUses()
    {
        // The gate's honest result, asserted rather than left as a silence. The checked-in
        // SCPD is a subset of the standardized WANIPConnection:1 template and declares five
        // of the seven actions InternetGateway calls: GetSpecificPortMappingEntry and
        // AddAnyPortMapping (an IGD:2 action, absent from a :1 document by definition) have
        // no generated counterpart.
        //
        // So InternetGateway does NOT adopt the generated wrapper in 6.0.0 - not because the
        // parity failed, but because the document is narrower than the hand-written surface.
        // This test fails the day the document grows, which is exactly when that decision
        // should be revisited.
        var generated = typeof(WanIpConnection)
            .GetMethods()
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("GetSpecificPortMappingEntryAsync", generated);
        Assert.DoesNotContain("AddAnyPortMappingAsync", generated);

        Assert.All(
            new[]
            {
                "AddPortMappingAsync", "DeletePortMappingAsync", "GetExternalIPAddressAsync",
                "GetStatusInfoAsync", "GetGenericPortMappingEntryAsync"
            },
            name => Assert.Contains(name, generated));
    }

    [Fact]
    public async Task GetGenericPortMappingEntry_ComposesTheSameEnvelopeAndAction()
    {
        var (hand, generated) = await CaptureAsync(
            async gateway =>
            {
                await foreach (var _ in gateway.GetPortMappingsAsync(TestContext.Current.CancellationToken))
                {
                    break;   // the first index is all this comparison needs
                }
            },
            wrapper => wrapper.GetGenericPortMappingEntryAsync(0, TestContext.Current.CancellationToken),
            "GetGenericPortMappingEntry");

        AssertIdentical(hand, generated);
    }

    /// <summary>
    /// Byte-for-byte, and on the SOAPACTION header too - a correct envelope sent under the
    /// wrong action name is refused by the device just the same.
    /// </summary>
    private static void AssertIdentical((string Action, string Body) hand, (string Action, string Body) generated)
    {
        Assert.Equal(hand.Action, generated.Action);
        Assert.Equal(
            Encoding.UTF8.GetBytes(hand.Body),
            Encoding.UTF8.GetBytes(generated.Body));
    }

    /// <summary>Runs the same action through both surfaces and returns what each put on the wire.</summary>
    private static async Task<((string Action, string Body) Hand, (string Action, string Body) Generated)> CaptureAsync(
        Func<InternetGateway, Task> viaHandWritten,
        Func<WanIpConnection, Task> viaGenerated,
        string action)
    {
        var hand = await CaptureHandWrittenAsync(viaHandWritten, action);
        var generated = await CaptureGeneratedAsync(viaGenerated, action);

        return (hand, generated);
    }

    private static async Task<(string Action, string Body)> CaptureHandWrittenAsync(
        Func<InternetGateway, Task> call, string action)
    {
        var (gateway, http, client) = await DiscoverAsync();
        using var _ = client;

        http.Map(ControlUrl, _ => (HttpStatusCode.OK, Response(action)));

        await SwallowAsync(() => call(gateway));

        return Captured(http, action);
    }

    private static async Task<(string Action, string Body)> CaptureGeneratedAsync(
        Func<WanIpConnection, Task> call, string action)
    {
        var (gateway, http, client) = await DiscoverAsync();
        using var _ = client;

        http.Map(ControlUrl, _ => (HttpStatusCode.OK, Response(action)));

        await SwallowAsync(() => call(new WanIpConnection(gateway.WanConnectionService)));

        return Captured(http, action);
    }

    /// <summary>
    /// What went out, regardless of whether the caller liked what came back. The comparison
    /// is about the request; the canned response only has to be parsable enough not to
    /// abort before the request was recorded.
    /// </summary>
    private static async Task SwallowAsync(Func<Task> call)
    {
        try
        {
            await call();
        }
        catch (UpnpException)
        {
        }
    }

    private static (string Action, string Body) Captured(FakeHttpHandler http, string action)
    {
        var (request, body) = http.Requests.Last(r => r.Request.RequestUri!.ToString() == ControlUrl);

        return (request.Headers.GetValues("SOAPACTION").Single(), body);
    }

    private static async Task<(InternetGateway Gateway, FakeHttpHandler Http, UpnpClient Client)> DiscoverAsync()
    {
        var controlPoint = new FakeControlPoint();
        var http = new FakeHttpHandler();
        var client = new UpnpClient(controlPoint, http.CreateClient());

        http.Map(Location, TestKit.Fixture("linksys_WAG200G_desc.xml"));

        var task = PortMapper.DiscoverGatewayAsync(
            client, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        controlPoint.Responses.OnNext(new SSDP.UPnP.PCL.Model.ReceivedMSearchResponse
        {
            Location = new Uri(Location),
            USN = SSDP.UPnP.PCL.Model.USN.Parse("uuid:gw::upnp:rootdevice").Value,
            LocalIpEndPoint = new IPEndPoint(IPAddress.Parse("192.168.1.42"), 1900)
        });

        var gateway = await task;
        Assert.NotNull(gateway);

        return (gateway, http, client);
    }

    private static string Response(string action) => $"""
        <?xml version="1.0"?>
        <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
          <s:Body>
            <u:{action}Response xmlns:u="urn:schemas-upnp-org:service:WANPPPConnection:1" />
          </s:Body>
        </s:Envelope>
        """;
}
