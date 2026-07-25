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
        public Exception? FailSubscribe;
        public TimeSpan GrantedTimeout { get; set; } = TimeSpan.FromMinutes(10);

        public Task<(string Sid, TimeSpan? Timeout)> SubscribeAsync(
            Uri eventSubUrl, Uri callback, TimeSpan requestedTimeout, CancellationToken ct)
        {
            SubscribeCount++;
            return FailSubscribe is { } failure
                ? Task.FromException<(string Sid, TimeSpan? Timeout)>(failure)
                : Task.FromResult(($"uuid:sid-{SubscribeCount}", (TimeSpan?)GrantedTimeout));
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

    private GenaSubscriptionSource CreateSource(bool autoResubscribe = true) =>
        CreateSourceWithLifetime(CancellationToken.None, autoResubscribe);

    private GenaSubscriptionSource CreateSourceWithLifetime(CancellationToken lifetime, bool autoResubscribe = true) => new(
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
        lifetime);

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

    [Theory]
    [InlineData(404)]   // advertised eventSubURL doesn't exist (Sonos /ssdp/notfound)
    [InlineData(405)]   // endpoint refuses the method (Sonos QPlay)
    [InlineData(410)]   // gone - definitionally permanent in HTTP
    [InlineData(501)]   // method not implemented
    public async Task PermanentSubscribeRefusal_SurfacesReason_AndTerminates(int status)
    {
        // These cannot succeed on retry - the device contradicts its own
        // description: the reason arrives as data, then the stream ends,
        // AutoResubscribe notwithstanding.
        _transport.FailSubscribe = new GenaHttpException($"The SUBSCRIBE request was refused with HTTP {status}.", status);
        var source = CreateSource();
        var events = new List<UpnpEvent>();
        Exception? error = null;
        using var subscription = source.Subscribe(events.Add, e => error = e, () => { });

        await WaitForAsync(() => error is not null);

        var refused = Assert.Single(events.OfType<SubscriptionRefused>());
        Assert.Equal(status, refused.HttpStatus);
        Assert.Contains("The refusal is permanent", refused.Reason);
        Assert.IsType<UpnpException>(error);
        Assert.Equal(1, _transport.SubscribeCount);  // no retry against a permanent refusal
    }

    [Fact]
    public async Task TransientSubscribeFailure_KeepsRetrying()
    {
        _transport.FailSubscribe = new GenaHttpException("The SUBSCRIBE request was refused with HTTP 503.", 503);
        var source = CreateSource();
        var events = new List<UpnpEvent>();
        using var subscription = source.Subscribe(events.Add);

        await WaitForAsync(() => events.OfType<RenewalFailed>().Any());

        _transport.FailSubscribe = null;
        _time.Advance(TimeSpan.FromSeconds(10));     // the engine's retry delay

        await WaitForAsync(() => events.OfType<Subscribed>().Any());
        Assert.Equal(2, _transport.SubscribeCount);
    }

    [Fact]
    public async Task EngineRestart_DoesNotReplayStaleState()
    {
        var source = CreateSource();
        var first = new List<UpnpEvent>();
        var s1 = source.Subscribe(first.Add);
        await WaitForAsync(() => _route is not null);
        await NotifyAsync(0, "Volume", "20");

        s1.Dispose();
        await WaitForAsync(() => _transport.UnsubscribeCount == 1);

        // The subscriber that restarts the engine has nothing to catch up on:
        // the previous run's state is stale, and a fresh SEQ 0 set is coming.
        var second = new List<UpnpEvent>();
        using var s2 = source.Subscribe(second.Add);
        await WaitForAsync(() => _transport.SubscribeCount == 2);

        Assert.DoesNotContain(second, e => e is PropertyChange { IsReplay: true });
    }

    [Fact]
    public async Task SeqWrap_ContinuesAtOne_NotZero()
    {
        // UDA 2.0 §4.2.3: the event key wraps from uint.MaxValue to 1. Auto-
        // resubscribe is off so the (legitimate) first gap doesn't restart the
        // attempt and the wrap expectation can be observed directly.
        var source = CreateSource(autoResubscribe: false);
        var events = new List<UpnpEvent>();
        using var subscription = source.Subscribe(events.Add);
        await WaitForAsync(() => _route is not null);

        await NotifyAsync(uint.MaxValue, "A", "1");  // expected 0: one real gap
        await NotifyAsync(1, "A", "2");              // the wrap target - no gap

        Assert.Single(events.OfType<GapDetected>());
    }

    [Fact]
    public async Task ObserverThrowingDuringEngineEmission_SurfacesAsOnError()
    {
        var source = CreateSource();
        Exception? error = null;
        using var subscription = source.Subscribe(
            _ => throw new InvalidOperationException("consumer bug"),
            e => error = e,
            () => { });

        await WaitForAsync(() => error is not null);

        Assert.IsType<UpnpException>(error);
    }

    [Fact]
    public async Task ConcurrentNotifies_KeepTheirPropertyBatchesContiguous()
    {
        // Review RX-1: two NOTIFYs handled concurrently (the callback listener
        // imposes no serialization) must not interleave their property batches.
        // SEQ is omitted so only contiguity is asserted, not ordering.
        var source = CreateSource();
        var events = new List<UpnpEvent>();
        using var subscription = source.Subscribe(events.Add);
        await WaitForAsync(() => _route is not null);

        static string Body(string prefix) =>
            "<e:propertyset xmlns:e=\"urn:schemas-upnp-org:event-1-0\">" +
            string.Concat(Enumerable.Range(1, 3).Select(i => $"<e:property><{prefix}{i}>v</{prefix}{i}></e:property>")) +
            "</e:propertyset>";

        var ct = TestContext.Current.CancellationToken;
        await Task.WhenAll(
            Task.Run(() => _route!(new NotifyRequest("uuid:sid-1", null, Body("A")), ct), ct),
            Task.Run(() => _route!(new NotifyRequest("uuid:sid-1", null, Body("B")), ct), ct));

        var prefixes = events.OfType<PropertyChange>().Select(change => change.Name[0]).ToList();
        Assert.Equal(6, prefixes.Count);
        Assert.Single(prefixes.Take(3).Distinct());          // AAA or BBB…
        Assert.Single(prefixes.Skip(3).Distinct());          // …then the other
    }

    [Fact]
    public void SubscribeAfterClientDisposal_CompletesImmediately()
    {
        // Review RX-3: a fresh engine on a disposed client would be born
        // canceled and the observer would wait forever - complete instead.
        using var lifetime = new CancellationTokenSource();
        lifetime.Cancel();
        var source = CreateSourceWithLifetime(lifetime.Token);

        var completed = false;
        using var subscription = source.Subscribe(_ => { }, _ => { }, () => completed = true);

        Assert.True(completed);
        Assert.Equal(0, _transport.SubscribeCount);
    }

    [Fact]
    public async Task ShutdownAsync_SaysGoodbye_AndCompletesObservers()
    {
        var source = CreateSource();
        var completed = false;
        using var subscription = source.Subscribe(_ => { }, _ => { }, () => completed = true);
        await WaitForAsync(() => _transport.SubscribeCount == 1);

        await source.ShutdownAsync();

        Assert.True(completed);
        Assert.Equal(1, _transport.UnsubscribeCount);
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
