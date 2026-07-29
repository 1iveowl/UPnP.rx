using System.Net;
using Microsoft.Extensions.Time.Testing;
using SSDP.UPnP.PCL.Model;
using UPnP.Rx.PortMapping;
using UPnP.Rx.Tests.TestHelpers;
using Xunit;
using static UPnP.Rx.Tests.TestHelpers.TestKit;

namespace UPnP.Rx.Tests;

public class PortMappingTests
{
    private const string Location = "http://192.168.1.1:49152/desc.xml";
    private const string ControlUrl = "http://192.168.1.1:49152/upnp/control/WANPPPConn1";
    private const string PppServiceType = "urn:schemas-upnp-org:service:WANPPPConnection:1";

    private static string ResponseEnvelope(string action, string serviceType, string innerXml = "") => $"""
        <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
          <s:Body><u:{action}Response xmlns:u="{serviceType}">{innerXml}</u:{action}Response></s:Body>
        </s:Envelope>
        """;

    private static string FaultEnvelope(int code, string description) => $"""
        <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
          <s:Body><s:Fault><faultcode>s:Client</faultcode><faultstring>UPnPError</faultstring>
            <detail><UPnPError xmlns="urn:schemas-upnp-org:control-1-0">
              <errorCode>{code}</errorCode><errorDescription>{description}</errorDescription>
            </UPnPError></detail></s:Fault></s:Body>
        </s:Envelope>
        """;

    private static string SoapAction(HttpRequestMessage request) =>
        request.Headers.GetValues("SOAPACTION").Single().Trim('"').Split('#')[1];

    private static async Task<(InternetGateway Gateway, FakeControlPoint ControlPoint, FakeHttpHandler Http, UpnpClient Client)>
        DiscoverGatewayAsync(FakeTimeProvider? timeProvider = null)
    {
        var controlPoint = new FakeControlPoint();
        var http = new FakeHttpHandler();
        var options = new UpnpClientOptions { TimeProvider = timeProvider ?? TimeProvider.System };
        var client = new UpnpClient(controlPoint, http.CreateClient(), options);

        http.Map(Location, Fixture("linksys_WAG200G_desc.xml"));

        var task = PortMapper.DiscoverGatewayAsync(client, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        controlPoint.Responses.OnNext(new ReceivedMSearchResponse
        {
            Location = new Uri(Location),
            USN = USN.Parse("uuid:gateway::urn:schemas-upnp-org:device:InternetGatewayDevice:1").Value,
            BOOTID = 1,
            LocalIpEndPoint = new IPEndPoint(IPAddress.Parse("192.168.1.42"), 1900)
        });

        var gateway = await task;
        Assert.NotNull(gateway);
        return (gateway, controlPoint, http, client);
    }

    // ---- Gateway discovery ----

    [Fact]
    public void DiscoverGateways_StreamsUsableGateways_DeduplicatedByDevice()
    {
        var controlPoint = new FakeControlPoint();
        var http = new FakeHttpHandler();
        using var client = new UpnpClient(controlPoint, http.CreateClient());
        http.Map(Location, Fixture("linksys_WAG200G_desc.xml"));

        var gateways = new List<InternetGateway>();
        using var subscription = PortMapper.DiscoverGateways(client).Subscribe(gateways.Add);

        static ReceivedMSearchResponse Announce() => new()
        {
            Location = new Uri(Location),
            USN = USN.Parse("uuid:gateway::urn:schemas-upnp-org:device:InternetGatewayDevice:1").Value,
            BOOTID = 1,
            LocalIpEndPoint = new IPEndPoint(IPAddress.Parse("192.168.1.42"), 1900)
        };

        controlPoint.Responses.OnNext(Announce());
        controlPoint.Responses.OnNext(Announce() with { BOOTID = 2 });   // reboot: new SSDP identity, same device

        var gateway = Assert.Single(gateways);                           // deduplicated by UDN
        Assert.Equal(PppServiceType, gateway.WanConnectionService.Description.ServiceType);
    }

    [Fact]
    public async Task DiscoverGateway_ResolvesWanServiceAndLocalAddress()
    {
        var (gateway, _, _, client) = await DiscoverGatewayAsync();
        using var _1 = client;

        Assert.Equal(PppServiceType, gateway.WanConnectionService.Description.ServiceType);
        Assert.Equal(IPAddress.Parse("192.168.1.42"), gateway.LocalAddress);
        Assert.Equal("LINKSYS WAG200G Gateway", gateway.Device.Description.FriendlyName);
    }

    [Fact]
    public async Task DiscoverGateway_TimesOutToNull()
    {
        var timeProvider = new FakeTimeProvider();
        var controlPoint = new FakeControlPoint();
        var http = new FakeHttpHandler();
        using var client = new UpnpClient(
            controlPoint, http.CreateClient(), new UpnpClientOptions { TimeProvider = timeProvider });

        var task = PortMapper.DiscoverGatewayAsync(client, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        timeProvider.Advance(TimeSpan.FromSeconds(6));

        Assert.Null(await task);
    }

    // ---- Actions ----

    [Fact]
    public async Task GetExternalIPAddress_ParsesTheAnswer()
    {
        var (gateway, _, http, client) = await DiscoverGatewayAsync();
        using var _1 = client;

        http.Map(ControlUrl, _ => (HttpStatusCode.OK, ResponseEnvelope(
            "GetExternalIPAddress", PppServiceType,
            "<NewExternalIPAddress>203.0.113.17</NewExternalIPAddress>")));

        Assert.Equal(
            IPAddress.Parse("203.0.113.17"),
            await gateway.GetExternalIPAddressAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetPortMappings_EnumeratesUntilTheGatewayFaults()
    {
        var (gateway, _, http, client) = await DiscoverGatewayAsync();
        using var _1 = client;

        var index = 0;
        http.Map(ControlUrl, _ => index++ < 2
            ? (HttpStatusCode.OK, ResponseEnvelope("GetGenericPortMappingEntry", PppServiceType, $"""
                <NewRemoteHost></NewRemoteHost><NewExternalPort>800{index}</NewExternalPort>
                <NewProtocol>TCP</NewProtocol><NewInternalPort>900{index}</NewInternalPort>
                <NewInternalClient>192.168.1.5{index}</NewInternalClient><NewEnabled>1</NewEnabled>
                <NewPortMappingDescription>entry {index}</NewPortMappingDescription>
                <NewLeaseDuration>3600</NewLeaseDuration>
                """))
            : (HttpStatusCode.InternalServerError, FaultEnvelope(713, "SpecifiedArrayIndexInvalid")));

        var mappings = new List<PortMappingEntry>();

        await foreach (var mapping in gateway.GetPortMappingsAsync(TestContext.Current.CancellationToken))
        {
            mappings.Add(mapping);
        }

        Assert.Equal(2, mappings.Count);
        Assert.Equal(8001, mappings[0].ExternalPort);
        Assert.Equal(Protocol.Tcp, mappings[0].Protocol);
        Assert.Equal(TimeSpan.FromHours(1), mappings[0].LeaseDuration);
    }

    [Fact]
    public async Task AddPortMapping_SendsSpecOrderedArguments_AndDefaultsInternalClient()
    {
        var (gateway, _, http, client) = await DiscoverGatewayAsync();
        using var _1 = client;

        http.Map(ControlUrl, _ => (HttpStatusCode.OK, ResponseEnvelope("AddPortMapping", PppServiceType)));

        await using var lease = await gateway.AddPortMappingAsync(
            18080, 18081, Protocol.Tcp, "test map", TimeSpan.Zero,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(18080, lease.Mapping.ExternalPort);
        Assert.Equal("192.168.1.42", lease.Mapping.InternalClient);   // defaulted from discovery

        var (request, body) = http.Requests.Last(r => r.Request.RequestUri!.ToString() == ControlUrl);
        Assert.Equal("AddPortMapping", SoapAction(request));
        Assert.Contains("<NewExternalPort>18080</NewExternalPort>", body);
        Assert.Contains("<NewInternalClient>192.168.1.42</NewInternalClient>", body);
        Assert.Contains("<NewLeaseDuration>0</NewLeaseDuration>", body);
    }

    // ---- Lease duration: the range, and the sentinel that used to swallow it ----

    [Theory]
    [InlineData(-5)]                 // saturates through (uint) to 0 == "permanent"
    [InlineData(-1)]
    [InlineData(604_801)]            // one second over the IGD template's maximum
    [InlineData(2_592_000)]          // 30 days
    public async Task AddPortMapping_LeaseOutsideIgdRange_IsRefusedRatherThanComposed(int seconds)
    {
        var (gateway, _, http, client) = await DiscoverGatewayAsync();
        using var _1 = client;

        http.Map(ControlUrl, _ => (HttpStatusCode.OK, ResponseEnvelope("AddPortMapping", PppServiceType)));

        var thrown = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            gateway.AddPortMappingAsync(
                18080, 18081, Protocol.Tcp, "out of range", TimeSpan.FromSeconds(seconds),
                ct: TestContext.Current.CancellationToken));

        // On the message: a test naming only the type would pass the day some other
        // argument guard starts throwing first.
        Assert.Contains("604800", thrown.Message, StringComparison.Ordinal);

        // And nothing reached the gateway - the point is that no AddPortMapping is
        // composed at all, rather than one composed with a mangled value.
        Assert.DoesNotContain(http.Requests, r => r.Request.RequestUri!.ToString() == ControlUrl);
    }

    [Fact]
    public async Task AddPortMapping_NegativeLease_WouldOtherwiseHaveAskedForAPermanentMapping()
    {
        // The specific reason the guard exists. .NET saturates floating-point to integer
        // conversions, so (uint)(-5.0) is 0 - and 0 is IGD's encoding for "never expires".
        // Asking for a five-second-ago lease used to compose <NewLeaseDuration>0</...>
        // and leave a permanent hole in the firewall, silently.
        Assert.Equal(0u, (uint)TimeSpan.FromSeconds(-5).TotalSeconds);
        Assert.False(LeaseDurations.IsValid(TimeSpan.FromSeconds(-5)));

        var (gateway, _, http, client) = await DiscoverGatewayAsync();
        using var _1 = client;
        http.Map(ControlUrl, _ => (HttpStatusCode.OK, ResponseEnvelope("AddPortMapping", PppServiceType)));

        // UPNPRX001 reports this literal, which is the rule working - the run-time guard
        // is the backstop for values it cannot see, and this test is the backstop's.
        // (Note the Theory above goes unreported: its values arrive as a parameter, so
        // source cannot see them. That is the accepted false-negative half of the budget.)
#pragma warning disable UPNPRX001 // Deliberate: asserting the run-time guard rejects it.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            gateway.AddPortMappingAsync(
                18080, 18081, Protocol.Tcp, "negative", TimeSpan.FromSeconds(-5),
                ct: TestContext.Current.CancellationToken));
#pragma warning restore UPNPRX001

        Assert.DoesNotContain(http.Requests, r => r.Request.RequestUri!.ToString() == ControlUrl);
    }

    [Fact]
    public async Task AddPortMapping_LeaseAtTheBoundaries_IsAccepted()
    {
        var (gateway, _, http, client) = await DiscoverGatewayAsync();
        using var _1 = client;

        http.Map(ControlUrl, _ => (HttpStatusCode.OK, ResponseEnvelope("AddPortMapping", PppServiceType)));

        await using var maximum = await gateway.AddPortMappingAsync(
            18080, 18081, Protocol.Tcp, "max", LeaseDurations.Maximum,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(LeaseDurations.Maximum, maximum.Mapping.LeaseDuration);
        Assert.Contains(
            "<NewLeaseDuration>604800</NewLeaseDuration>",
            http.Requests.Last(r => r.Request.RequestUri!.ToString() == ControlUrl).Body);
    }

    [Fact]
    public async Task GetSpecificPortMappingEntry_UnreportedLease_IsUnknownRatherThanIndefinite()
    {
        // TimeSpan.Zero means "never expires" in IGD, so using it as the parse-failure
        // sentinel made a gateway that omitted the field look like one promising a
        // permanent mapping. Null says what actually happened: it did not tell us.
        var (gateway, _, http, client) = await DiscoverGatewayAsync();
        using var _1 = client;

        http.Map(ControlUrl, _ => (HttpStatusCode.OK, ResponseEnvelope(
            "GetSpecificPortMappingEntry", PppServiceType,
            """
            <NewInternalPort>not-a-number</NewInternalPort>
            <NewInternalClient>192.168.1.50</NewInternalClient>
            <NewEnabled>1</NewEnabled>
            <NewPortMappingDescription>silent</NewPortMappingDescription>
            """)));

        var mapping = await gateway.GetSpecificPortMappingEntryAsync(
            8080, Protocol.Tcp, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(mapping);
        Assert.Null(mapping.LeaseDuration);      // not TimeSpan.Zero
        Assert.Null(mapping.InternalPort);       // not (ushort)0
    }

    [Fact]
    public async Task AddPortMapping_WildcardDiscoveryAddress_ResolvesInternalClientFromRoute()
    {
        // macOS/Linux reality: the SSDP envelope reports the wildcard-bound
        // socket (0.0.0.0), so the default internal client must come from a
        // route lookup toward the gateway - never 0.0.0.0 on the wire.
        var controlPoint = new FakeControlPoint();
        var http = new FakeHttpHandler();
        using var client = new UpnpClient(controlPoint, http.CreateClient());

        http.Map(Location, Fixture("linksys_WAG200G_desc.xml"));
        http.Map(ControlUrl, _ => (HttpStatusCode.OK, ResponseEnvelope("AddPortMapping", PppServiceType)));

        var task = PortMapper.DiscoverGatewayAsync(client, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        controlPoint.Responses.OnNext(new ReceivedMSearchResponse
        {
            Location = new Uri(Location),
            USN = USN.Parse("uuid:gateway::urn:schemas-upnp-org:device:InternetGatewayDevice:1").Value,
            BOOTID = 1,
            LocalIpEndPoint = new IPEndPoint(IPAddress.Any, 1900)
        });

        var gateway = await task;
        Assert.NotNull(gateway);
        Assert.Null(gateway.LocalAddress);

        await using var lease = await gateway.AddPortMappingAsync(
            18090, 18090, Protocol.Tcp, "wildcard test", TimeSpan.Zero,
            ct: TestContext.Current.CancellationToken);

        Assert.True(IPAddress.TryParse(lease.Mapping.InternalClient, out var sent));
        Assert.False(sent!.Equals(IPAddress.Any));
    }

    [Fact]
    public async Task GetStatusInfo_ParsesConnectionState()
    {
        var (gateway, _, http, client) = await DiscoverGatewayAsync();
        using var _1 = client;

        http.Map(ControlUrl, _ => (HttpStatusCode.OK, ResponseEnvelope("GetStatusInfo", PppServiceType, """
            <NewConnectionStatus>Connected</NewConnectionStatus>
            <NewLastConnectionError>ERROR_NONE</NewLastConnectionError>
            <NewUptime>7200</NewUptime>
            """)));

        var status = await gateway.GetStatusInfoAsync(TestContext.Current.CancellationToken);

        Assert.True(status.IsConnected);
        Assert.Equal("ERROR_NONE", status.LastError);
        Assert.Equal(TimeSpan.FromHours(2), status.Uptime);
    }

    [Fact]
    public async Task GetSpecificPortMappingEntry_ReturnsTheMapping_AndNullOn714()
    {
        var (gateway, _, http, client) = await DiscoverGatewayAsync();
        using var _1 = client;

        var exists = true;
        http.Map(ControlUrl, _ => exists
            ? (HttpStatusCode.OK, ResponseEnvelope("GetSpecificPortMappingEntry", PppServiceType, """
                <NewInternalPort>9090</NewInternalPort>
                <NewInternalClient>192.168.1.50</NewInternalClient>
                <NewEnabled>1</NewEnabled>
                <NewPortMappingDescription>existing</NewPortMappingDescription>
                <NewLeaseDuration>600</NewLeaseDuration>
                """))
            : (HttpStatusCode.InternalServerError, FaultEnvelope(714, "NoSuchEntryInArray")));

        var mapping = await gateway.GetSpecificPortMappingEntryAsync(
            8080, Protocol.Tcp, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(mapping);
        Assert.Equal((ushort)9090, mapping.InternalPort);
        Assert.Equal("192.168.1.50", mapping.InternalClient);

        exists = false;

        Assert.Null(await gateway.GetSpecificPortMappingEntryAsync(
            8080, Protocol.Tcp, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AddAnyPortMapping_RequiresWanIpConnection2()
    {
        var (gateway, _, _, client) = await DiscoverGatewayAsync();
        using var _1 = client;

        await Assert.ThrowsAsync<UpnpException>(() => gateway.AddAnyPortMappingAsync(
            18080, 18080, Protocol.Tcp, "x", TimeSpan.Zero, ct: TestContext.Current.CancellationToken));
    }

    // ---- The auto-renewing lease (decision 3) ----

    private static async Task<(PortMappingLease Lease, FakeTimeProvider Time, FakeHttpHandler Http, UpnpClient Client, List<PortMappingEvent> Events)>
        LeaseWithRenewalAsync(Func<string, bool>? failWhen = null)
    {
        var timeProvider = new FakeTimeProvider();
        var (gateway, _, http, client) = await DiscoverGatewayAsync(timeProvider);

        http.Map(ControlUrl, request =>
        {
            var action = SoapAction(request);

            return failWhen?.Invoke(action) == true
                ? (HttpStatusCode.InternalServerError, FaultEnvelope(501, "ActionFailed"))
                : (HttpStatusCode.OK, ResponseEnvelope(action, PppServiceType));
        });

        var lease = await gateway.AddPortMappingAsync(
            18080, 18080, Protocol.Tcp, "renewing", TimeSpan.FromSeconds(100));

        var events = new List<PortMappingEvent>();
        lease.Events.Subscribe(events.Add);

        return (lease, timeProvider, http, client, events);
    }

    private static int RenewalCount(FakeHttpHandler http) =>
        http.Requests.Count(r =>
            r.Request.RequestUri!.ToString() == ControlUrl
            && r.Request.Headers.Contains("SOAPACTION")
            && SoapAction(r.Request) == "AddPortMapping");

    [Fact]
    public async Task Lease_RenewsAtHalfLife_OnTheInjectedClock()
    {
        var (lease, time, http, client, events) = await LeaseWithRenewalAsync();
        using var _1 = client;
        await using var _2 = lease;

        Assert.Equal(1, RenewalCount(http));                  // the initial add

        time.Advance(TimeSpan.FromSeconds(50));               // half-life tick
        await WaitForAsync(() => RenewalCount(http) == 2);

        time.Advance(TimeSpan.FromSeconds(50));
        await WaitForAsync(() => RenewalCount(http) == 3);

        await WaitForAsync(() => events.Count(e => e.Kind == PortMappingEventKind.Renewed) == 2);
    }

    [Fact]
    public async Task Lease_RenewalFailure_IsDataAndRetries_ThenExpires()
    {
        var failRenewals = false;
        var (lease, time, http, client, events) = await LeaseWithRenewalAsync(
            action => failRenewals && action == "AddPortMapping");
        using var _1 = client;
        await using var _2 = lease;

        failRenewals = true;

        time.Advance(TimeSpan.FromSeconds(50));               // fail #1 (elapsed 50 ≤ lease)
        await WaitForAsync(() => events.Count(e => e.Kind == PortMappingEventKind.RenewalFailed) == 1);
        Assert.DoesNotContain(events, e => e.Kind == PortMappingEventKind.Expired);

        time.Advance(TimeSpan.FromSeconds(50));               // fail #2 (elapsed 100, not yet past lease)
        await WaitForAsync(() => events.Count(e => e.Kind == PortMappingEventKind.RenewalFailed) == 2);

        time.Advance(TimeSpan.FromSeconds(50));               // fail #3 (elapsed 150 > lease) → Expired once
        await WaitForAsync(() => events.Count(e => e.Kind == PortMappingEventKind.Expired) == 1);

        failRenewals = false;                                 // recovery → Renewed again
        time.Advance(TimeSpan.FromSeconds(50));
        await WaitForAsync(() => events.Any(e => e.Kind == PortMappingEventKind.Renewed));
    }

    [Fact]
    public async Task DisposeAsync_DeletesTheMappingAndStopsRenewal()
    {
        var (lease, time, http, client, _) = await LeaseWithRenewalAsync();
        using var _1 = client;

        await lease.DisposeAsync();

        Assert.Contains(http.Requests, r =>
            r.Request.RequestUri!.ToString() == ControlUrl && SoapAction(r.Request) == "DeletePortMapping");

        var renewalsAtDisposal = RenewalCount(http);
        time.Advance(TimeSpan.FromSeconds(200));
        Assert.Equal(renewalsAtDisposal, RenewalCount(http));  // the loop is gone
    }

    [Fact]
    public async Task Dispose_IsAbrupt_NoDeleteNoFurtherRenewals()
    {
        var (lease, time, http, client, _) = await LeaseWithRenewalAsync();
        using var _1 = client;

        lease.Dispose();

        Assert.DoesNotContain(http.Requests, r =>
            r.Request.RequestUri!.ToString() == ControlUrl
            && r.Request.Headers.Contains("SOAPACTION")
            && SoapAction(r.Request) == "DeletePortMapping");

        time.Advance(TimeSpan.FromSeconds(200));
        Assert.Equal(1, RenewalCount(http));                   // only the initial add ever happened
    }

    // ---- AddAnyPortMapping on a genuine WANIPConnection:2 gateway ----

    [Fact]
    public async Task AddAnyPortMapping_UsesTheGrantedPort()
    {
        const string igd2Location = "http://10.0.0.1/igd2.xml";
        const string igd2Control = "http://10.0.0.1/ctl/WANIPConn2";
        const string igd2Service = "urn:schemas-upnp-org:service:WANIPConnection:2";

        var controlPoint = new FakeControlPoint();
        var http = new FakeHttpHandler();
        using var client = new UpnpClient(controlPoint, http.CreateClient());

        http.Map(igd2Location, $"""
            <?xml version="1.0"?>
            <root xmlns="urn:schemas-upnp-org:device-1-0">
              <specVersion><major>2</major><minor>0</minor></specVersion>
              <device>
                <deviceType>urn:schemas-upnp-org:device:InternetGatewayDevice:2</deviceType>
                <friendlyName>IGD2</friendlyName><UDN>uuid:igd2</UDN>
                <serviceList><service>
                  <serviceType>{igd2Service}</serviceType>
                  <serviceId>urn:upnp-org:serviceId:WANIPConn2</serviceId>
                  <controlURL>/ctl/WANIPConn2</controlURL>
                  <SCPDURL>/scpd.xml</SCPDURL>
                </service></serviceList>
              </device>
            </root>
            """);
        http.Map(igd2Control, _ => (HttpStatusCode.OK, ResponseEnvelope(
            "AddAnyPortMapping", igd2Service, "<NewReservedPort>18099</NewReservedPort>")));

        var task = PortMapper.DiscoverGatewayAsync(client, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        controlPoint.Responses.OnNext(new ReceivedMSearchResponse
        {
            Location = new Uri(igd2Location),
            USN = USN.Parse("uuid:igd2::urn:schemas-upnp-org:device:InternetGatewayDevice:2").Value,
            LocalIpEndPoint = new IPEndPoint(IPAddress.Parse("10.0.0.50"), 1900)
        });

        var gateway = await task;
        Assert.NotNull(gateway);

        await using var lease = await gateway.AddAnyPortMappingAsync(
            18080, 18080, Protocol.Tcp, "any", TimeSpan.Zero, ct: TestContext.Current.CancellationToken);

        Assert.Equal(18099, lease.Mapping.ExternalPort);       // the granted, shifted port
    }
}
