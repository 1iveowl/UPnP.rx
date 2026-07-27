using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using UPnP.Rx.Eventing;
using Xunit;
using static UPnP.Rx.Tests.TestHelpers.TestKit;

namespace UPnP.Rx.Tests;

/// <summary>
/// End-to-end over real loopback sockets (no multicast involved): a scripted
/// GENA "device" on System.Net.HttpListener, the real HttpGenaTransport, the
/// real SimpleHttpListener.Rx-backed callback listener, and the real engine.
/// </summary>
public sealed class EventingLoopbackTests : IDisposable
{
    private readonly HttpListener _device = new();
    private readonly int _devicePort;
    private string? _callbackUrl;
    private string? _lastMethod;
    private readonly TaskCompletionSource<string> _unsubscribed = new();

    public EventingLoopbackTests()
    {
        // Find a free port for the fake device.
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        _devicePort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        _device.Prefixes.Add($"http://127.0.0.1:{_devicePort}/");
        _device.Start();
        _ = RunDeviceAsync();
    }

    private async Task RunDeviceAsync()
    {
        while (_device.IsListening)
        {
            HttpListenerContext context;

            try
            {
                context = await _device.GetContextAsync();
            }
            catch (Exception)
            {
                return;                     // listener closed - test over
            }

            _lastMethod = context.Request.HttpMethod;

            if (context.Request.HttpMethod == "SUBSCRIBE" && context.Request.Headers["SID"] is null)
            {
                _callbackUrl = context.Request.Headers["CALLBACK"]?.Trim('<', '>');
                context.Response.AddHeader("SID", "uuid:loopback-sub-1");
                context.Response.AddHeader("TIMEOUT", "Second-1800");
            }
            else if (context.Request.HttpMethod == "UNSUBSCRIBE")
            {
                _unsubscribed.TrySetResult(context.Request.Headers["SID"] ?? "");
            }

            context.Response.StatusCode = 200;
            context.Response.Close();
        }
    }

    private async Task NotifyAsync(uint seq, string body)
    {
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(new HttpMethod("NOTIFY"), _callbackUrl);
        request.Headers.TryAddWithoutValidation("NT", "upnp:event");
        request.Headers.TryAddWithoutValidation("NTS", "upnp:propchange");
        request.Headers.TryAddWithoutValidation("SID", "uuid:loopback-sub-1");
        request.Headers.TryAddWithoutValidation("SEQ", seq.ToString());
        request.Content = new StringContent(body, Encoding.UTF8);

        using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SubscribeNotifyUnsubscribe_OverRealSockets()
    {
        var options = new UpnpClientOptions();
        using var httpClient = new HttpClient();
        using var lifetime = new CancellationTokenSource();
        using var listener = EventCallbackListener.Create(0, NullLogger.Instance, lifetime.Token);
        var transport = new HttpGenaTransport(httpClient, options);

        var source = new GenaSubscriptionSource(
            new Uri($"http://127.0.0.1:{_devicePort}/event"),
            token => new Uri($"http://127.0.0.1:{listener.Port}/upnp/events/{token}"),
            transport,
            listener.Register,
            options,
            NullLogger.Instance,
            Identity(),
            NoPresence,
            lifetime.Token);

        var events = new List<UpnpEvent>();
        var subscription = source.Subscribe(events.Add);

        await WaitForRealTimeAsync(() => _callbackUrl is not null && events.OfType<Subscribed>().Any());

        await NotifyAsync(0,
            "<e:propertyset xmlns:e=\"urn:schemas-upnp-org:event-1-0\"><e:property><TransportState>PLAYING</TransportState></e:property></e:propertyset>");

        await WaitForRealTimeAsync(() => events.OfType<PropertyChange>().Any());

        var change = Assert.Single(events.OfType<PropertyChange>());
        Assert.Equal(("TransportState", "PLAYING", true), (change.Name, change.Value, change.IsInitialState));

        subscription.Dispose();

        var sid = await _unsubscribed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal("uuid:loopback-sub-1", sid);
        Assert.Equal("UNSUBSCRIBE", _lastMethod);
    }

    [Fact]
    public async Task WildcardDiscoveryAddress_CallbackHostComesFromRouteLookup()
    {
        // macOS/Linux reality: the discovery envelope carries the wildcard
        // address (0.0.0.0), because the SSDP socket is wildcard-bound. The
        // callback host must then come from a route lookup toward the device -
        // devices refuse CALLBACK: <http://0.0.0.0:…> with HTTP 412.
        var options = new UpnpClientOptions();
        using var httpClient = new HttpClient();
        using var lifetime = new CancellationTokenSource();
        using var eventing = new EventingContext(httpClient, options, NoPresence, lifetime.Token);

        var events = new List<UpnpEvent>();
        using var subscription = eventing
            .GetOrCreateSource(new Uri($"http://127.0.0.1:{_devicePort}/event"), IPAddress.Any, Identity())
            .Subscribe(events.Add);

        await WaitForRealTimeAsync(() => _callbackUrl is not null && events.OfType<Subscribed>().Any());

        Assert.StartsWith("http://127.0.0.1:", _callbackUrl);
    }

    public void Dispose()
    {
        _device.Stop();
        _device.Close();
    }
}
