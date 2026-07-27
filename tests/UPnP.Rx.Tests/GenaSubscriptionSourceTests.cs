using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using UPnP.Rx.Eventing;
using UPnP.Rx.Presence;
using Xunit;
using System.Reactive.Subjects;
using static UPnP.Rx.Tests.TestHelpers.TestKit;

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



        /// <summary>When set, SUBSCRIBE blocks on this until completed or the attempt is cancelled.</summary>
        public TaskCompletionSource? HoldSubscribe;

        /// <summary>
        /// Whether a held SUBSCRIBE aborts when its attempt is cancelled. True models a
        /// request torn down promptly; false models a response already on the wire when
        /// the cancellation lands - the race the stale-cancellation test needs.
        /// </summary>
        public bool HoldObservesCancellation = true;
        public TimeSpan GrantedTimeout { get; set; } = TimeSpan.FromMinutes(10);

        public async Task<(string Sid, TimeSpan? Timeout)> SubscribeAsync(
            Uri eventSubUrl, Uri callback, TimeSpan requestedTimeout, CancellationToken ct)
        {
            if (HoldSubscribe is { } hold)
            {
                SubscribeCount++;

                if (HoldObservesCancellation)
                {
                    await hold.Task.WaitAsync(ct);
                }
                else
                {
                    await hold.Task;
                }
            }
            else
            {
                // A cancelled attempt never puts a request on the wire, so it does not
                // count as one.
                ct.ThrowIfCancellationRequested();
                SubscribeCount++;
            }

            return FailSubscribe is { } failure
                ? throw failure
                : ($"uuid:sid-{SubscribeCount}", (TimeSpan?)GrantedTimeout);
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
    private Action? _onRouteRegistered;

    /// <summary>Drives the presence notices UDA 2.0 clause 4.1.1 says cancel a subscription.</summary>
    private readonly Subject<UPnP.Rx.Presence.RosterChange> _roster = new();

    private Func<IObservable<UPnP.Rx.Presence.RosterChange>> _presence => () => _roster;

    /// <summary>
    /// The engine subscribes to presence off the caller's stack (deliberately - it
    /// would otherwise take the roster's gate under its own). A Subject does not
    /// replay, so tests must wait for the subscription before pushing into it.
    /// </summary>
    private Task PresenceReadyAsync() => WaitForRealTimeAsync(() => _roster.HasObservers);

    private GenaSubscriptionSource CreateSource(bool autoResubscribe = true) =>
        CreateSourceWithLifetime(CancellationToken.None, autoResubscribe);

    private GenaSubscriptionSource CreateSourceWithLifetime(CancellationToken lifetime, bool autoResubscribe = true) => new(
        new Uri("http://192.168.1.9/event/sub"),
        token => new Uri($"http://192.168.1.42:49500/upnp/events/{token}"),
        _transport,
        (_, handler) =>
        {
            _route = handler;
            _onRouteRegistered?.Invoke();
            return System.Reactive.Disposables.Disposable.Create(() => _route = null);
        },
        new UpnpClientOptions { TimeProvider = _time, AutoResubscribe = autoResubscribe },
        NullLogger.Instance,
        Identity(),
        _presence,
        lifetime);

    private Task NotifyAsync(uint seq, string name, string value) =>
        _route!(new NotifyRequest("uuid:sid-1", seq,
            $"<e:propertyset xmlns:e=\"urn:schemas-upnp-org:event-1-0\"><e:property><{name}>{value}</{name}></e:property></e:propertyset>"),
            CancellationToken.None);

    // UDA 2.0 clause 4.1.1: "If the publisher cancels its advertisements or if the
    // value of the BOOTID.UPNP.ORG is increased without a prior ssdp:update message
    // with a matching NEXTBOOTID.UPNP.ORG field value, subscribers shall assume that
    // their subscriptions have been cancelled."

    private static DiscoveredDevice Device(string udn = "uuid:device-1", int? configId = 1) =>
        DiscoveredFor(udn, configId);

    [Fact]
    public async Task DeviceReboots_SubscriptionIsAssumedCancelled_AndResubscribedFresh()
    {
        var source = CreateSource();
        var events = new List<UpnpEvent>();
        using var subscription = source.Subscribe(events.Add);
        await WaitForAsync(() => _transport.SubscribeCount == 1);
        await PresenceReadyAsync();

        _roster.OnNext(new DeviceRebooted(Device()));
        await WaitForAsync(() => _transport.SubscribeCount == 2);

        var cancelled = Assert.Single(events.OfType<SubscriptionCancelled>());
        Assert.True(cancelled.WillResubscribe);
        Assert.Contains("restarted", cancelled.Reason);

        // Recovery is a fresh SUBSCRIBE (clause 4.1.2), never a renewal on the old SID.
        Assert.Equal(2, _transport.SubscribeCount);
        Assert.Equal(0, _transport.RenewCount);

        // And no goodbye: the same clause makes the SID void, and says the publisher
        // "shall reject" any non-subscribe message carrying it.
        Assert.Equal(0, _transport.UnsubscribeCount);
    }

    [Fact]
    public async Task DeviceSaysByeBye_SubscriptionEnds_WithoutUnsubscribing()
    {
        var source = CreateSource();
        var events = new List<UpnpEvent>();
        Exception? error = null;
        using var subscription = source.Subscribe(events.Add, e => error = e);
        await WaitForAsync(() => _transport.SubscribeCount == 1);
        await PresenceReadyAsync();

        _roster.OnNext(new DeviceLeft(Device()));
        await WaitForAsync(() => error is not null);

        var cancelled = Assert.Single(events.OfType<SubscriptionCancelled>());
        Assert.False(cancelled.WillResubscribe);          // the device is gone; retrying is pointless
        Assert.Contains("withdrew", cancelled.Reason);
        Assert.Equal(1, _transport.SubscribeCount);
        Assert.Equal(0, _transport.UnsubscribeCount);
    }

    [Fact]
    public async Task RebootWithAChangedConfigId_EndsInsteadOfResubscribing()
    {
        // Clause 4.1.2 requires subscribing to the eventSubURL the description
        // advertises; clause 1.2.2 makes an unchanged CONFIGID the guarantee that the
        // description has not moved. Without that guarantee the cached URL may be
        // stale, so the stream ends rather than subscribing to a URL we cannot vouch for.
        var source = CreateSource();
        var events = new List<UpnpEvent>();
        Exception? error = null;
        using var subscription = source.Subscribe(events.Add, e => error = e);
        await WaitForAsync(() => _transport.SubscribeCount == 1);
        await PresenceReadyAsync();

        _roster.OnNext(new DeviceRebooted(Device(configId: 2)));
        await WaitForAsync(() => error is not null);

        Assert.False(Assert.Single(events.OfType<SubscriptionCancelled>()).WillResubscribe);
        Assert.Equal(1, _transport.SubscribeCount);
        Assert.Equal(0, _transport.UnsubscribeCount);
    }

    [Fact]
    public async Task PresenceCancellationDuringTheRetryBackoff_IsNotLost()
    {
        // The engine parks for 10 s after a failed SUBSCRIBE. A byebye arriving in that
        // window used to be dropped: the retry jumped straight back to the loop head,
        // skipping the cancellation check, and the next iteration cleared the flag - so
        // the engine kept issuing SUBSCRIBEs at a device that had left the network.
        _transport.FailSubscribe = new UpnpException("device busy");

        var source = CreateSource();
        var events = new List<UpnpEvent>();
        Exception? error = null;
        using var subscription = source.Subscribe(events.Add, e => error = e);
        await WaitForAsync(() => _transport.SubscribeCount == 1);
        await PresenceReadyAsync();

        _roster.OnNext(new DeviceLeft(Device()));
        await WaitForAsync(() => error is not null);

        var cancelled = Assert.Single(events.OfType<SubscriptionCancelled>());
        Assert.False(cancelled.WillResubscribe);
        Assert.Equal(1, _transport.SubscribeCount);
    }

    [Fact]
    public async Task CancellationBeforeTheFirstSubscribeSucceeds_StillReportsSubscribedNotResubscribed()
    {
        // A device restarting while the very first SUBSCRIBE is still in flight -
        // routine when it reboots during a discovery sweep - must not consume that
        // first establishment. Resubscribed carries no granted timeout, so a consumer
        // that never sees Subscribed never learns the subscription's duration.
        _transport.HoldSubscribe = new TaskCompletionSource();

        var source = CreateSource();
        var events = new List<UpnpEvent>();
        using var subscription = source.Subscribe(events.Add, _ => { });
        await WaitForAsync(() => _transport.SubscribeCount == 1);
        await PresenceReadyAsync();

        // Let the retry through; only the in-flight attempt is cancelled.
        _transport.HoldSubscribe = null;

        // A restart with an unchanged CONFIGID: void but recoverable.
        _roster.OnNext(new DeviceRebooted(Device()));
        await WaitForAsync(() => events.OfType<SubscriptionCancelled>().Any());
        await WaitForAsync(() => events.OfType<Subscribed>().Any() || events.OfType<Resubscribed>().Any());

        Assert.Empty(events.OfType<Resubscribed>());
        Assert.Equal(TimeSpan.FromMinutes(10), Assert.Single(events.OfType<Subscribed>()).Timeout);
    }

    [Fact]
    public async Task ACancellationBankedByADyingAttempt_DoesNotPoisonTheNextRun()
    {
        // The interleaving: a byebye lands while a SUBSCRIBE is in flight, and the
        // device's answer - already on the wire - is a permanent 501. The refusal path
        // ends the stream without consuming the banked notice, which must not then be
        // applied to a later, healthy run: it would suppress that run's UNSUBSCRIBE and
        // kill it with a stale "withdrew its advertisements" reason.
        _transport.HoldSubscribe = new TaskCompletionSource();
        _transport.HoldObservesCancellation = false;
        _transport.FailSubscribe = new GenaHttpException("refused", 501);

        var source = CreateSource();
        Exception? firstError = null;
        using (source.Subscribe(_ => { }, e => firstError = e))
        {
            await WaitForAsync(() => _transport.SubscribeCount == 1);
            await PresenceReadyAsync();
            _roster.OnNext(new DeviceLeft(Device()));      // banked; the attempt is held
            _transport.HoldSubscribe.SetResult();          // now the 501 lands
            await WaitForAsync(() => firstError is not null);
        }

        // A fresh run against a healthy device; an ordinary renewal failure must
        // resubscribe, not surface last run's departure.
        _transport.HoldSubscribe = null;
        _transport.FailSubscribe = null;
        var events = new List<UpnpEvent>();
        Exception? secondError = null;
        using var second = source.Subscribe(events.Add, e => secondError = e);
        await WaitForAsync(() => events.OfType<Subscribed>().Any());

        _transport.FailRenewals = true;
        _time.Advance(TimeSpan.FromMinutes(5));
        await WaitForAsync(() => events.OfType<RenewalFailed>().Any());
        _transport.FailRenewals = false;
        await WaitForAsync(() => events.OfType<Resubscribed>().Any());

        Assert.Null(secondError);
        Assert.Empty(events.OfType<SubscriptionCancelled>());
    }

    [Fact]
    public async Task ANoticeArrivingBetweenAttempts_IsActedOnByTheNextAttempt()
    {
        // Between attempts no CancellationTokenSource is published, so a notice has
        // nothing to cancel. Route registration is the one deterministic moment inside
        // that gap, so the byebye is injected exactly there - without the publish-then-
        // recheck, the engine would SUBSCRIBE afresh at the departed device and park in
        // its renewal loop for half the granted timeout.
        var registrations = 0;
        _onRouteRegistered = () =>
        {
            if (Interlocked.Increment(ref registrations) == 2)
            {
                _roster.OnNext(new DeviceLeft(Device()));
            }
        };

        var source = CreateSource();
        var events = new List<UpnpEvent>();
        Exception? error = null;
        using var subscription = source.Subscribe(events.Add, e => error = e);
        await WaitForAsync(() => events.OfType<Subscribed>().Any());
        await PresenceReadyAsync();

        // End attempt 1; attempt 2's route registration fires the injection.
        _transport.FailRenewals = true;
        _time.Advance(TimeSpan.FromMinutes(5));
        await WaitForAsync(() => error is not null);

        Assert.False(Assert.Single(events.OfType<SubscriptionCancelled>()).WillResubscribe);
        Assert.Equal(1, _transport.SubscribeCount);      // no fresh SUBSCRIBE at a departed device
    }

    [Fact]
    public async Task PresenceChangesForOtherDevices_AreIgnored()
    {
        var source = CreateSource();
        var events = new List<UpnpEvent>();
        using var subscription = source.Subscribe(events.Add);
        await WaitForAsync(() => _transport.SubscribeCount == 1);
        await PresenceReadyAsync();

        _roster.OnNext(new DeviceLeft(Device("uuid:someone-else")));
        _roster.OnNext(new DeviceRebooted(Device("uuid:someone-else")));
        await SettleAsync();

        Assert.Empty(events.OfType<SubscriptionCancelled>());
        Assert.Equal(1, _transport.SubscribeCount);
    }

    [Fact]
    public async Task DeviceUpdated_DoesNotCancel_TheSubscriptionSurvivesADescriptionChange()
    {
        // Only the two events the clause names cancel a subscription. A device whose
        // description changed while it kept running has not lost its subscriptions -
        // and an announced config change (ssdp:update) never reaches here at all,
        // because the roster resolves it to no change.
        var source = CreateSource();
        var events = new List<UpnpEvent>();
        using var subscription = source.Subscribe(events.Add);
        await WaitForAsync(() => _transport.SubscribeCount == 1);
        await PresenceReadyAsync();

        _roster.OnNext(new DeviceUpdated(Device()));
        await SettleAsync();

        Assert.Empty(events.OfType<SubscriptionCancelled>());
        Assert.Equal(1, _transport.SubscribeCount);
    }

    [Fact]
    public async Task WithAutoResubscribeOff_ARebootEndsTheStream()
    {
        var source = CreateSource(autoResubscribe: false);
        var events = new List<UpnpEvent>();
        Exception? error = null;
        using var subscription = source.Subscribe(events.Add, e => error = e);
        await WaitForAsync(() => _transport.SubscribeCount == 1);
        await PresenceReadyAsync();

        _roster.OnNext(new DeviceRebooted(Device()));
        await WaitForAsync(() => error is not null);

        Assert.False(Assert.Single(events.OfType<SubscriptionCancelled>()).WillResubscribe);
        Assert.Equal(1, _transport.SubscribeCount);
    }

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
