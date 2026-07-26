using System.Net;
using SSDP.UPnP.PCL.Model;
using UPnP.Rx.Tests.TestHelpers;
using Xunit;
using static UPnP.Rx.Tests.TestHelpers.TestKit;

namespace UPnP.Rx.Tests;

public class UpnpClientTests
{
    private const string Location = "http://192.168.1.1:49152/desc.xml";

    private static MSearchResponse Response(
        string usn = "uuid:device-1::upnp:rootdevice", uint bootId = 1, TimeSpan cacheControl = default) => new()
    {
        Location = new Uri(Location),
        USN = USN.Parse(usn).Value,
        BOOTID = bootId,
        CacheControl = cacheControl
    };

    private static (UpnpClient Client, FakeControlPoint ControlPoint, FakeHttpHandler Http) CreateClient(
        params IPAddress[] addresses)
    {
        var controlPoint = new FakeControlPoint();
        var http = new FakeHttpHandler();
        var client = new UpnpClient(controlPoint, http.CreateClient(), options: null, addresses);
        return (client, controlPoint, http);
    }

    // ---- Discovery ----

    [Fact]
    public void DiscoverDevices_EmitsFromMSearchResponses()
    {
        var (client, controlPoint, _) = CreateClient();
        using var _1 = client;

        var seen = new List<DiscoveredDevice>();
        using var subscription = client.DiscoverDevices().Subscribe(seen.Add);

        controlPoint.Responses.OnNext(Response());

        var device = Assert.Single(seen);
        Assert.Equal(new Uri(Location), device.Location);
        Assert.Equal(1u, device.BootSignature.BootId);
    }

    [Fact]
    public void DiscoverDevices_DeduplicatesByUsnAndBootId()
    {
        var (client, controlPoint, _) = CreateClient();
        using var _1 = client;

        var seen = new List<DiscoveredDevice>();
        using var subscription = client.DiscoverDevices().Subscribe(seen.Add);

        controlPoint.Responses.OnNext(Response());
        controlPoint.Responses.OnNext(Response());                    // duplicate announcement
        controlPoint.Responses.OnNext(Response(bootId: 2));           // reboot → new identity

        Assert.Equal(2, seen.Count);
    }

    [Fact]
    public void DiscoverDevices_MergesAliveNotifies()
    {
        var (client, controlPoint, _) = CreateClient();
        using var _1 = client;

        var seen = new List<DiscoveredDevice>();
        using var subscription = client.DiscoverDevices().Subscribe(seen.Add);

        controlPoint.Notifies.OnNext(new Notify
        {
            NTS = NTS.Alive,
            Location = new Uri(Location),
            USN = USN.Parse("uuid:device-2::upnp:rootdevice").Value,
            BOOTID = 7
        });

        var device = Assert.Single(seen);
        Assert.Equal(7u, device.BootSignature.BootId);
    }

    [Fact]
    public void DiscoverDevices_WildcardLocalEndPoint_SurfacesAsUnknown()
    {
        var (client, controlPoint, _) = CreateClient();
        using var _1 = client;

        var seen = new List<DiscoveredDevice>();
        using var subscription = client.DiscoverDevices().Subscribe(seen.Add);

        // macOS/Linux reality: the SSDP socket is wildcard-bound, so upstream
        // reports 0.0.0.0:1900 as the receiving endpoint. That must never
        // surface as "our address" - it once became CALLBACK: <http://0.0.0.0:…>
        // and devices refused the SUBSCRIBE with HTTP 412.
        controlPoint.Notifies.OnNext(new Notify
        {
            NTS = NTS.Alive,
            Location = new Uri(Location),
            USN = USN.Parse("uuid:device-3::upnp:rootdevice").Value,
            BOOTID = 1,
            LocalIpEndPoint = new IPEndPoint(IPAddress.Any, 1900)
        });

        var device = Assert.Single(seen);
        Assert.Null(device.LocalEndPoint);
    }

    [Fact]
    public void DiscoverDevices_DropsAnnouncementsWithoutLocation()
    {
        var (client, controlPoint, _) = CreateClient();
        using var _1 = client;

        var seen = new List<DiscoveredDevice>();
        using var subscription = client.DiscoverDevices().Subscribe(seen.Add);

        controlPoint.Responses.OnNext(new MSearchResponse { USN = USN.Parse("uuid:x::upnp:rootdevice").Value });

        Assert.Empty(seen);
    }

    [Fact]
    public void DiscoverDevices_SendsSearchPerAddress_WithCpfnAndDefaults()
    {
        var (client, controlPoint, _) = CreateClient(IPAddress.Parse("10.0.0.2"), IPAddress.Parse("10.0.0.3"));
        using var _1 = client;

        using var subscription = client.DiscoverDevices().Subscribe(_ => { });

        Assert.Equal(2, controlPoint.SentSearches.Count);

        var (request, address) = controlPoint.SentSearches[0];
        Assert.Equal(IPAddress.Parse("10.0.0.2"), address);
        Assert.Equal("UPnP.Rx", request.CPFN);                                   // UDA 2.0 requires CPFN
        Assert.Equal("upnp:rootdevice", request.ST.ToSearchTargetString());       // decision 6 default
        Assert.Equal(TimeSpan.FromSeconds(3), request.MX);
    }

    [Fact]
    public void DiscoverDevices_PerCallSearchTargetOverridesOptions()
    {
        var (client, controlPoint, _) = CreateClient(IPAddress.Loopback);
        using var _1 = client;

        using var subscription = client
            .DiscoverDevices(SearchTargets.ServiceType("WANIPConnection", 2), TimeSpan.FromSeconds(5))
            .Subscribe(_ => { });

        var (request, _) = Assert.Single(controlPoint.SentSearches);
        Assert.Equal("urn:schemas-upnp-org:service:WANIPConnection:2", request.ST.ToSearchTargetString());
        Assert.Equal(TimeSpan.FromSeconds(5), request.MX);
    }

    [Fact]
    public void DeviceLost_EmitsFromByeByes_WithoutLocation()
    {
        var (client, controlPoint, _) = CreateClient();
        using var _1 = client;

        var lost = new List<DiscoveredDevice>();
        using var subscription = client.DeviceLost().Subscribe(lost.Add);

        controlPoint.Notifies.OnNext(new Notify
        {
            NTS = NTS.ByeBye,
            USN = USN.Parse("uuid:device-1::upnp:rootdevice").Value
        });

        var device = Assert.Single(lost);
        Assert.Null(device.Location);
        Assert.Equal("uuid:device-1", device.Usn?.ToUsnString().Split("::")[0]);
    }

    [Fact]
    public void DiscoverDescribedDevices_EmitsDescribed_SkipsFailures_DedupsByUdn()
    {
        var (client, controlPoint, http) = CreateClient();
        using var _1 = client;
        http.Map(Location, Fixture("new_LiveBox_desc.xml"));

        var seen = new List<DescribedDevice>();
        using var subscription = client.DiscoverDescribedDevices().Subscribe(seen.Add);

        controlPoint.Responses.OnNext(Response());
        controlPoint.Responses.OnNext(Response(bootId: 2));            // same device, rebooted → dedup by UDN
        controlPoint.Responses.OnNext(new MSearchResponse               // unfetchable → skipped, stream lives
        {
            Location = new Uri("http://192.168.1.99/nope.xml"),
            USN = USN.Parse("uuid:broken::upnp:rootdevice").Value
        });

        var device = Assert.Single(seen);
        Assert.Equal("Orange Livebox", device.Description.FriendlyName);
    }

    // ---- Description fetch + cache ----

    [Fact]
    public async Task GetDescriptionAsync_FetchesParsesAndCaches_WithinOneBoot()
    {
        var (client, controlPoint, http) = CreateClient();
        await using var _1 = client;
        http.Map(Location, Fixture("new_LiveBox_desc.xml"));

        var seen = new List<DiscoveredDevice>();
        using var subscription = client.DiscoverDevices().Subscribe(seen.Add);

        // Root and embedded entities announce the same LOCATION within one boot.
        controlPoint.Responses.OnNext(Response());
        controlPoint.Responses.OnNext(Response(usn: "uuid:device-1::urn:schemas-upnp-org:device:WANDevice:2"));

        var first = await seen[0].GetDescriptionAsync(TestContext.Current.CancellationToken);
        var second = await seen[1].GetDescriptionAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Orange Livebox", first.Description.FriendlyName);
        Assert.Same(first, second);                            // cached within the boot
        Assert.Equal(1, http.FetchCounts[Location]);           // one fetch for both
    }

    [Fact]
    public async Task GetDescriptionAsync_RefetchesAfterReboot()
    {
        // The UPnP 1.0 installed base never sends CONFIGID - without BOOTID in
        // the cache key, a sparse description served mid-boot (seen on Sonos)
        // would stick for the client's lifetime.
        var (client, controlPoint, http) = CreateClient();
        await using var _1 = client;
        http.Map(Location, Fixture("new_LiveBox_desc.xml"));

        var seen = new List<DiscoveredDevice>();
        using var subscription = client.DiscoverDevices().Subscribe(seen.Add);

        controlPoint.Responses.OnNext(Response(bootId: 1));
        var first = await seen[0].GetDescriptionAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Orange Livebox", first.Description.FriendlyName);

        http.Map(Location, Fixture("linksys_WAG200G_desc.xml"));   // the device "recovered" with new content
        controlPoint.Responses.OnNext(Response(bootId: 2));        // reboot

        var second = await seen[1].GetDescriptionAsync(TestContext.Current.CancellationToken);

        Assert.Equal("LINKSYS WAG200G Gateway", second.Description.FriendlyName);
        Assert.Equal(2, http.FetchCounts[Location]);
    }

    [Fact]
    public async Task GetDescriptionAsync_ExpiresWithTheAnnouncementsMaxAge()
    {
        // Within one boot, a cached description expires when the SSDP
        // advertisement's CACHE-CONTROL max-age elapses - so a sparse read
        // served mid-boot heals by the next advertisement cycle, not the next
        // reboot. One fake clock drives it all (time model rule 5).
        var timeProvider = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var controlPoint = new FakeControlPoint();
        var http = new FakeHttpHandler();
        await using var client = new UpnpClient(
            controlPoint, http.CreateClient(), new UpnpClientOptions { TimeProvider = timeProvider });
        http.Map(Location, Fixture("new_LiveBox_desc.xml"));

        var seen = new List<DiscoveredDevice>();
        using var subscription = client.DiscoverDevices().Subscribe(seen.Add);
        controlPoint.Responses.OnNext(Response(cacheControl: TimeSpan.FromMinutes(30)));

        await seen[0].GetDescriptionAsync(TestContext.Current.CancellationToken);
        timeProvider.Advance(TimeSpan.FromMinutes(29));
        await seen[0].GetDescriptionAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, http.FetchCounts[Location]);           // still fresh

        timeProvider.Advance(TimeSpan.FromMinutes(2));         // past max-age
        await seen[0].GetDescriptionAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, http.FetchCounts[Location]);           // re-read
    }

    [Fact]
    public async Task InvalidateDescriptions_ForcesTheNextFetch()
    {
        var (client, controlPoint, http) = CreateClient();
        await using var _1 = client;
        http.Map(Location, Fixture("new_LiveBox_desc.xml"));

        var seen = new List<DiscoveredDevice>();
        using var subscription = client.DiscoverDevices().Subscribe(seen.Add);
        controlPoint.Responses.OnNext(Response());

        await seen[0].GetDescriptionAsync(TestContext.Current.CancellationToken);
        client.InvalidateDescriptions(new Uri(Location));
        await seen[0].GetDescriptionAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, http.FetchCounts[Location]);
    }

    [Fact]
    public async Task GetDescriptionAsync_FailedFetch_IsNotCachedForever()
    {
        // Review finding: negative caching. A transient failure must be retried
        // on the next call, not poison the device for the client's lifetime.
        var (client, controlPoint, http) = CreateClient();
        await using var _1 = client;
        // Location intentionally unmapped → 404 → HttpRequestException → UpnpException.

        var seen = new List<DiscoveredDevice>();
        using var subscription = client.DiscoverDevices().Subscribe(seen.Add);
        controlPoint.Responses.OnNext(Response());

        await Assert.ThrowsAsync<UpnpException>(
            () => seen[0].GetDescriptionAsync(TestContext.Current.CancellationToken));

        http.Map(Location, Fixture("new_LiveBox_desc.xml"));            // device recovers

        var described = await seen[0].GetDescriptionAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Orange Livebox", described.Description.FriendlyName);
    }

    [Fact]
    public async Task GetScpdAsync_FailedFetch_IsRetriedOnNextCall()
    {
        var (client, controlPoint, http) = CreateClient();
        await using var _1 = client;
        http.Map(Location, Fixture("linksys_WAG200G_desc.xml"));

        var device = await DescribedLinksysAsync(controlPoint, client);
        var wan = device.Service("WANPPPConnection");
        // SCPD URL intentionally unmapped → failure…

        await Assert.ThrowsAsync<UpnpException>(() => wan.GetScpdAsync(TestContext.Current.CancellationToken));

        http.Map("http://192.168.1.1:49152/pppcfg.xml", Fixture("wanipconnection1_scpd.xml"));

        var scpd = await wan.GetScpdAsync(TestContext.Current.CancellationToken);
        Assert.Contains(scpd.Actions, a => a.Name == "AddPortMapping");
    }

    [Fact]
    public async Task GetDescriptionAsync_UnparsableDocument_ThrowsUpnpException()
    {
        var (client, controlPoint, http) = CreateClient();
        await using var _1 = client;
        http.Map(Location, "utter garbage");

        var seen = new List<DiscoveredDevice>();
        using var subscription = client.DiscoverDevices().Subscribe(seen.Add);
        controlPoint.Responses.OnNext(Response());

        var exception = await Assert.ThrowsAsync<UpnpException>(
            () => seen[0].GetDescriptionAsync(TestContext.Current.CancellationToken));
        Assert.Contains("unparsable", exception.Message);
    }

    // ---- Service resolution + control ----

    private async Task<DescribedDevice> DescribedLinksysAsync(FakeControlPoint controlPoint, UpnpClient client)
    {
        var seen = new List<DiscoveredDevice>();
        using var subscription = client.DiscoverDevices().Subscribe(seen.Add);
        controlPoint.Responses.OnNext(Response());
        return await seen[0].GetDescriptionAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Service_MatchesByBareTypeNameAcrossTheTree()
    {
        var (client, controlPoint, http) = CreateClient();
        await using var _1 = client;
        http.Map(Location, Fixture("linksys_WAG200G_desc.xml"));

        var device = await DescribedLinksysAsync(controlPoint, client);

        Assert.True(device.HasService("WANPPPConnection"));    // nested two levels deep
        Assert.True(device.HasService("urn:schemas-upnp-org:service:Layer3Forwarding:1"));
        Assert.True(device.HasService("urn:upnp-org:serviceId:WANCommonIFC1"));
        Assert.False(device.HasService("WANIPConnection"));    // this router is PPP-only
        Assert.NotNull(device.TryService("WANPPPConnection"));
        Assert.Null(device.TryService("WANIPConnection"));     // the non-throwing lookup
        Assert.Equal(5, device.Services.Count);

        Assert.Throws<UpnpException>(() => device.Service("NoSuchService"));
    }

    [Fact]
    public async Task InvokeAsync_PostsSoapAndParsesOutArguments()
    {
        var (client, controlPoint, http) = CreateClient();
        await using var _1 = client;
        http.Map(Location, Fixture("linksys_WAG200G_desc.xml"));
        http.Map("http://192.168.1.1:49152/upnp/control/WANPPPConn1", """
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
              <s:Body>
                <u:GetExternalIPAddressResponse xmlns:u="urn:schemas-upnp-org:service:WANPPPConnection:1">
                  <NewExternalIPAddress>203.0.113.17</NewExternalIPAddress>
                </u:GetExternalIPAddressResponse>
              </s:Body>
            </s:Envelope>
            """);

        var device = await DescribedLinksysAsync(controlPoint, client);
        var wan = device.Service("WANPPPConnection");

        var result = await wan.InvokeAsync("GetExternalIPAddress", ct: TestContext.Current.CancellationToken);

        Assert.Equal("203.0.113.17", result["NewExternalIPAddress"]);

        var (request, body) = http.Requests.Last();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(
            "\"urn:schemas-upnp-org:service:WANPPPConnection:1#GetExternalIPAddress\"",
            request.Headers.GetValues("SOAPACTION").Single());
        Assert.Contains("GetExternalIPAddress", body);

        // UDA 2.0 §3.2.1: quoted charset (compliance review finding 2) and
        // product-token USER-AGENT (finding 3).
        Assert.Equal(
            "text/xml; charset=\"utf-8\"",
            request.Content!.Headers.GetValues("Content-Type").Single());
        Assert.Contains("UPnP/2.0", string.Join(" ", request.Headers.GetValues("USER-AGENT")));
    }

    [Fact]
    public async Task InvokeAsync_SoapFault_ThrowsUpnpActionExceptionWithError()
    {
        var (client, controlPoint, http) = CreateClient();
        await using var _1 = client;
        http.Map(Location, Fixture("linksys_WAG200G_desc.xml"));
        http.Map("http://192.168.1.1:49152/upnp/control/WANPPPConn1", _ => (HttpStatusCode.InternalServerError, """
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
              <s:Body><s:Fault><faultcode>s:Client</faultcode><faultstring>UPnPError</faultstring>
                <detail><UPnPError xmlns="urn:schemas-upnp-org:control-1-0">
                  <errorCode>718</errorCode><errorDescription>ConflictInMappingEntry</errorDescription>
                </UPnPError></detail></s:Fault></s:Body>
            </s:Envelope>
            """));

        var device = await DescribedLinksysAsync(controlPoint, client);
        var wan = device.Service("WANPPPConnection");

        var exception = await Assert.ThrowsAsync<UpnpActionException>(
            () => wan.InvokeAsync("AddPortMapping", ct: TestContext.Current.CancellationToken));

        Assert.Equal(718, exception.Error.Code);
        Assert.Equal("ConflictInMappingEntry", exception.Error.Description);
    }

    [Fact]
    public async Task GetScpdAsync_FetchesParsesAndCaches()
    {
        var (client, controlPoint, http) = CreateClient();
        await using var _1 = client;
        http.Map(Location, Fixture("linksys_WAG200G_desc.xml"));
        http.Map("http://192.168.1.1:49152/pppcfg.xml", Fixture("wanipconnection1_scpd.xml"));

        var device = await DescribedLinksysAsync(controlPoint, client);
        var wan = device.Service("WANPPPConnection");

        var scpd = await wan.GetScpdAsync(TestContext.Current.CancellationToken);
        await wan.GetScpdAsync(TestContext.Current.CancellationToken);

        Assert.Contains(scpd.Actions, a => a.Name == "AddPortMapping");
        Assert.Equal(1, http.FetchCounts["http://192.168.1.1:49152/pppcfg.xml"]);
    }

    // ---- Lifecycle ----

    [Fact]
    public void DiscoverDevices_AfterDispose_Throws()
    {
        var (client, _, _) = CreateClient();
        client.Dispose();

        Assert.Throws<ObjectDisposedException>(() => client.DiscoverDevices().Subscribe(_ => { }));
    }
}
