using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using UPnP.Rx.Eventing;
using Xunit;

namespace UPnP.Rx.Tests;

public class GenaSubscriptionSourceTests
{
    private sealed class FakeTransport : IGenaTransport
    {
        public int SubscribeCount;
        public int RenewCount;
        public int UnsubscribeCount;
        public bool FailRenewals;
        public TimeSpan GrantedTimeout { get; set; } = TimeSpan.FromMinutes(10);

        public Task<(string Sid, TimeSpan? Timeout)> SubscribeAsync(
            Uri eventSubUrl, Uri callback, TimeSpan requestedTimeout, CancellationToken ct)
        {
            SubscribeCount++;
            return Task.FromResult(($"uuid:sid-{SubscribeCount}", (TimeSpan?)GrantedTimeout));
        }

        public Task RenewAsync(Uri eventSubUrl, string sid, TimeSpan requestedTimeout, CancellationToken ct)
        {
            RenewCount++;
            return FailRenewals
                ? Task.FromException(new UpnpException("renewal refused"))
                : Task.CompletedTask;
        }

        public Task UnsubscribeAsync(Uri eventSubUrl, string sid, CancellationToken ct)
        {
            UnsubscribeCount++;
            return Task.CompletedTask;
        }
    }

    private readonly FakeTransport _transport = new();
    private readonly FakeTimeProvider _time = new();
    private Func<NotifyRequest, CancellationToken, Task>? _route;

    private GenaSubscriptionSource CreateSource(bool autoResubscribe = true) => new(
        new Uri("http://192.168.1.9/event/sub"),
        token => new Uri($"http://192.168.1.42:49500/upnp/events/{token}"),
        _transport,
        (_, handler) =>
        {
            _route = handler;
            return System.Reactive.Disposables.Disposable.Create(() => _route = null);
        },
        new UpnpClientOptions { TimeProvider = _time, AutoResubscribe = autoResubscribe },
        NullLogger.Instance,
        CancellationToken.None);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100_000 && !condition(); i++)
        {
            await Task.Yield();
        }

        Assert.True(condition(), "The condition was not reached.");
    }

    private Task NotifyAsync(uint seq, string name, string value) =>
        _route!(new NotifyRequest("uuid:sid-1", seq,
            $"<e:propertyset xmlns:e=\"urn:schemas-upnp-org:event-1-0\"><e:property><{name}>{value}</{name}></e:property></e:propertyset>"),
            CancellationToken.None);

    [Fact]
    public async Task FirstSubscriber_StartsTheEngine_InitialStateFlows()
    {
        var source = CreateSource();
        var events = new List<UpnpEvent>();

        using var subscription = source.Subscribe(events.Add);
        await WaitForAsync(() => _route is not null && _transport.SubscribeCount == 1);

        await NotifyAsync(0, "TransportState", "PLAYING");

        var subscribed = Assert.IsType<Subscribed>(events[0]);
        Assert.Equal("uuid:sid-1", subscribed.Sid);
        var change = Assert.IsType<PropertyChange>(events[1]);
        Assert.Equal(("TransportState", "PLAYING", 0u, true, false),
            (change.Name, change.Value, change.Seq, change.IsInitialState, change.IsReplay));
    }

    [Fact]
    public async Task LateSubscriber_GetsLastKnownState_AsReplay()
    {
        var source = CreateSource();
        var first = new List<UpnpEvent>();
        using var s1 = source.Subscribe(first.Add);
        await WaitForAsync(() => _route is not null);

        await NotifyAsync(0, "Volume", "20");
        await NotifyAsync(1, "Volume", "35");

        var late = new List<UpnpEvent>();
        using var s2 = source.Subscribe(late.Add);

        var replay = Assert.IsType<PropertyChange>(Assert.Single(late));
        Assert.Equal(("Volume", "35", true), (replay.Name, replay.Value, replay.IsReplay));

        await NotifyAsync(2, "Volume", "40");
        Assert.Equal("40", Assert.IsType<PropertyChange>(late[1]).Value);
    }

    [Fact]
    public async Task RenewsAtHalfLife_OnTheInjectedClock()
    {
        var source = CreateSource();
        using var subscription = source.Subscribe(_ => { });
        await WaitForAsync(() => _transport.SubscribeCount == 1);

        _time.Advance(TimeSpan.FromMinutes(5));      // half of the granted 10 min
        await WaitForAsync(() => _transport.RenewCount == 1);

        _time.Advance(TimeSpan.FromMinutes(5));
        await WaitForAsync(() => _transport.RenewCount == 2);
    }

    [Fact]
    public async Task RenewalFailure_SurfacesAndResubscribes()
    {
        var source = CreateSource();
        var events = new List<UpnpEvent>();
        using var subscription = source.Subscribe(events.Add);
        await WaitForAsync(() => _transport.SubscribeCount == 1);

        _transport.FailRenewals = true;
        _time.Advance(TimeSpan.FromMinutes(5));
        await WaitForAsync(() => _transport.UnsubscribeCount == 1 && _transport.SubscribeCount == 2);

        Assert.Contains(events, e => e is RenewalFailed);
        Assert.Contains(events, e => e is Resubscribed { Sid: "uuid:sid-2" });
    }

    [Fact]
    public async Task SeqGap_TriggersGapDetected_AndResubscribe()
    {
        var source = CreateSource();
        var events = new List<UpnpEvent>();
        using var subscription = source.Subscribe(events.Add);
        await WaitForAsync(() => _route is not null);

        await NotifyAsync(0, "A", "1");
        await NotifyAsync(3, "A", "4");              // 1 and 2 were lost

        await WaitForAsync(() => _transport.SubscribeCount == 2);

        var gap = Assert.Single(events.OfType<GapDetected>());
        Assert.Equal((1u, 3u), (gap.ExpectedSeq, gap.ActualSeq));
    }

    [Fact]
    public async Task LastDisposal_StopsTheEngine_WithUnsubscribe()
    {
        var source = CreateSource();
        var subscription = source.Subscribe(_ => { });
        await WaitForAsync(() => _transport.SubscribeCount == 1);

        subscription.Dispose();

        await WaitForAsync(() => _transport.UnsubscribeCount == 1);
    }

    [Fact]
    public async Task AutoResubscribeOff_RenewalFailure_TerminatesWithError()
    {
        var source = CreateSource(autoResubscribe: false);
        Exception? error = null;
        using var subscription = source.Subscribe(_ => { }, e => error = e, () => { });
        await WaitForAsync(() => _transport.SubscribeCount == 1);

        _transport.FailRenewals = true;
        _time.Advance(TimeSpan.FromMinutes(5));

        await WaitForAsync(() => error is not null);
        Assert.IsType<UpnpException>(error);
        Assert.Equal(1, _transport.SubscribeCount);  // no resubscribe attempt
    }
}
