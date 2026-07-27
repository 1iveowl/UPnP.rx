using System.Reactive.Concurrency;
using System.Reactive.Linq;
using Microsoft.Extensions.Logging;
using UPnP.Rx.Presence;

namespace UPnP.Rx.Eventing;

/// <summary>
/// One shared GENA subscription per service: N Rx subscribers, one SUBSCRIBE on
/// the device. The Rx subscription IS the GENA subscription - the first
/// subscriber starts the engine, the last disposal stops it (UNSUBSCRIBE runs
/// in the engine task's teardown, never fire-and-forget from Dispose). A gate
/// serializes every emission and guards the last-known-value snapshot that late
/// subscribers receive as replay (plan decisions Q1/Q2/Q5).
/// </summary>
internal sealed class GenaSubscriptionSource : EngineSource<UpnpEvent>
{
    private readonly Uri _eventSubUrl;
    private readonly Func<string, Uri> _callbackForToken;
    private readonly IGenaTransport _transport;
    private readonly Func<string, Func<NotifyRequest, CancellationToken, Task>, IDisposable> _registerRoute;
    private readonly UpnpClientOptions _options;
    private readonly ILogger _logger;

    private static readonly TimeSpan _retryDelay = TimeSpan.FromSeconds(10);

    private readonly Dictionary<string, PropertyChange> _lastKnown = new(StringComparer.OrdinalIgnoreCase);

    private readonly DeviceIdentity _identity;
    private readonly Func<IObservable<RosterChange>> _presence;

    /// <summary>Why a subscription was abandoned, and whether a fresh one may follow.</summary>
    private sealed record Cancellation(string Reason, bool MayResubscribe);

    /// <summary>
    /// The attempt currently running, so a presence notice can end it. Published
    /// without the gate deliberately - see <see cref="OnPresenceChange"/>.
    /// </summary>
    private CancellationTokenSource? _attempt;

    /// <summary>Set when the device's presence says this subscription is void; also gate-free.</summary>
    private Cancellation? _cancelled;

    internal GenaSubscriptionSource(
        Uri eventSubUrl,
        Func<string, Uri> callbackForToken,
        IGenaTransport transport,
        Func<string, Func<NotifyRequest, CancellationToken, Task>, IDisposable> registerRoute,
        UpnpClientOptions options,
        ILogger logger,
        DeviceIdentity identity,
        Func<IObservable<RosterChange>> presence,
        CancellationToken clientLifetime)
        : base(clientLifetime)
    {
        _identity = identity;
        _presence = presence;
        _eventSubUrl = eventSubUrl;
        _callbackForToken = callbackForToken;
        _transport = transport;
        _registerRoute = registerRoute;
        _options = options;
        _logger = logger;
    }

    /// <summary>A fresh run: anything remembered predates the stop and is superseded by the new SEQ 0 state.</summary>
    protected override void ClearStateLocked() => _lastKnown.Clear();

    /// <summary>Q5: late subscribers get the last-known state first, flagged as replay.</summary>
    protected override void ReplayLocked(IObserver<UpnpEvent> observer)
    {
        foreach (var change in _lastKnown.Values)
        {
            observer.OnNext(change with { IsReplay = true });
        }
    }

    protected override async Task RunEngineAsync(CancellationToken ct)
    {
        try
        {
            await RunAttemptsAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Engine stop between attempts - the regular way out.
        }
        catch (Exception e)
        {
            // The one legitimate OnError: the engine itself died unexpectedly
            // (e.g. an observer threw during an engine-context emission).
            // Silence here would leave subscribers waiting forever.
            _logger.EventEngineFailed(e, _eventSubUrl);
            Error(new UpnpException($"The event subscription engine for {_eventSubUrl} failed unexpectedly: {e.Message}", e));
        }
    }

    private async Task RunAttemptsAsync(CancellationToken ct)
    {
        var everSubscribed = false;

        // UDA 2.0 clause 4.1.1 strongly recommends that subscribers watch the
        // publisher's discovery messages; the roster already derives both events the
        // clause names - a byebye and an unannounced BOOTID change - and already
        // honours the ssdp:update exclusion, so this observes it rather than tracking
        // boot identities a second time. It also means an event subscription keeps the
        // roster engine running for as long as it lives.
        // SubscribeOn is load-bearing, not decoration. EngineSource starts this engine
        // while holding THIS source's gate, and subscribing to the roster acquires the
        // roster's gate - a second, different lock, under which the roster in turn emits
        // into OnPresenceChange. Taking them in both orders is a deadlock cycle, so the
        // roster subscription is pushed off the caller's stack; it also keeps SSDP
        // socket setup and the roster's opening M-SEARCH from happening under a lock.
        using var presence = _identity.Uuid is null
            ? System.Reactive.Disposables.Disposable.Empty
            : _presence()
                .SubscribeOn(DefaultScheduler.Instance)
                .Subscribe(
                    OnPresenceChange,
                    e => _logger.PresenceWatchEnded(e, _eventSubUrl));

        while (!ct.IsCancellationRequested)
        {
            // A fresh token per attempt: NOTIFYs route by token, so a NOTIFY
            // racing ahead of the SUBSCRIBE response (real Sonos behavior) is
            // still ours, and stale NOTIFYs for earlier attempts fall into 412.
            var token = Guid.NewGuid().ToString("N");
            using var resubscribe = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var seqTracker = new SeqTracker();
            string? sid = null;

            using var route = _registerRoute(token, (notify, _) =>
            {
                HandleNotify(notify, seqTracker, resubscribe);
                return Task.CompletedTask;
            });

            Volatile.Write(ref _attempt, resubscribe);

            try
            {
                TimeSpan granted;

                try
                {
                    var result = await _transport
                        .SubscribeAsync(_eventSubUrl, _callbackForToken(token), _options.EventSubscriptionTimeout, resubscribe.Token)
                        .ConfigureAwait(false);

                    sid = result.Sid;
                    granted = result.Timeout ?? _options.EventSubscriptionTimeout;
                }
                catch (UpnpException e) when (!ct.IsCancellationRequested)
                {
                    if (e is GenaHttpException { StatusCode: 404 or 405 or 410 or 501 } refusal)
                    {
                        // These refusals are permanent, not Sonos-specific quirk
                        // handling: 405/501 mean the endpoint exists but will
                        // never speak SUBSCRIBE; 404/410 mean the device denies
                        // the very URL its own description advertised (410 is
                        // definitionally permanent in HTTP). Retrying cannot
                        // heal a self-contradiction - a device that fixes its
                        // description re-announces, which yields a fresh stream.
                        // So the reason is surfaced as data and the stream ends,
                        // regardless of AutoResubscribe (which exists for
                        // recoverable failures). Everything else - 5xx, 412,
                        // timeouts - keeps the retry posture.
                        var explanation = refusal.StatusCode is 404 or 410
                            ? "the eventSubURL its own description advertises does not exist on the device"
                            : "the service advertises an eventSubURL but does not implement eventing";
                        var reason =
                            $"The device refused SUBSCRIBE with HTTP {refusal.StatusCode}: {explanation}. " +
                            "This is a device quirk - UDA 2.0 requires a non-evented service to publish " +
                            "an empty eventSubURL. The refusal is permanent, so the stream ends instead " +
                            "of retrying.";

                        Emit(new SubscriptionRefused(refusal.StatusCode, reason));
                        Error(new UpnpException(reason, e));
                        return;
                    }

                    Emit(new RenewalFailed($"SUBSCRIBE failed: {e.Message}"));

                    if (!_options.AutoResubscribe)
                    {
                        Error(new UpnpException($"The event subscription to {_eventSubUrl} could not be established: {e.Message}", e));
                        return;
                    }

                    // On the attempt's token, not the engine's, so a presence notice
                    // arriving during the wait cuts the backoff short. Its cancellation
                    // is swallowed here rather than allowed to escape: thrown from
                    // inside a catch block it would bypass this try's sibling handlers
                    // and end the engine silently, with the notice never acted on.
                    try
                    {
                        await Task.Delay(_retryDelay, _options.TimeProvider, resubscribe.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // The device's presence changed; handled immediately below.
                    }

                    // Clause 4.1.1 ends the stream for a departed device rather than
                    // retrying at it every 10 s forever.
                    if (ConsumeCancellation() is CancellationOutcome.Ended)
                    {
                        return;
                    }

                    continue;
                }

                // Subscribed marks the first successful establishment - a retried
                // initial SUBSCRIBE is not a "re"-subscription to the consumer.
                Emit(everSubscribed ? new Resubscribed(sid) : new Subscribed(sid, granted));
                everSubscribed = true;

                // Renew at half-life on the one clock; never faster than 1 s.
                var period = TimeSpan.FromTicks(Math.Max(granted.Ticks / 2, TimeSpan.TicksPerSecond));
                using var timer = new PeriodicTimer(period, _options.TimeProvider);

                while (await timer.WaitForNextTickAsync(resubscribe.Token).ConfigureAwait(false))
                {
                    try
                    {
                        await _transport
                            .RenewAsync(_eventSubUrl, sid, _options.EventSubscriptionTimeout, resubscribe.Token)
                            .ConfigureAwait(false);
                    }
                    catch (UpnpException e)
                    {
                        Emit(new RenewalFailed(e.Message));

                        if (!_options.AutoResubscribe)
                        {
                            Error(new UpnpException($"The event subscription {sid} could not be renewed: {e.Message}", e));
                            return;
                        }

                        break;               // leave the renewal loop → fresh SUBSCRIBE
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Engine stop or an intentional resubscribe trigger - both fine.
            }
            finally
            {
                // Peek, never take: the decision below the finally consumes it.
                var cancelled = Volatile.Read(ref _cancelled);

                Volatile.Write(ref _attempt, null);

                // Teardown runs INSIDE the engine task: say goodbye when we had a live
                // subscription and the device is presumably still there. Not when its
                // presence says otherwise - clause 4.1.1 makes the SID void at that
                // point, and the publisher "shall reject" any non-subscribe message
                // carrying it, so UNSUBSCRIBE would be a message we know is refused.
                if (sid is not null && cancelled is null)
                {
                    try
                    {
                        using var goodbye = new CancellationTokenSource(_options.ActionTimeout, _options.TimeProvider);
                        await _transport.UnsubscribeAsync(_eventSubUrl, sid, goodbye.Token).ConfigureAwait(false);
                    }
                    catch (Exception e)
                    {
                        _logger.UnsubscribeFailed(e, sid);
                    }
                }
            }

            if (ConsumeCancellation() is CancellationOutcome.Ended)
            {
                return;
            }
        }
    }

    private enum CancellationOutcome
    {
        /// <summary>No presence notice is pending.</summary>
        None,

        /// <summary>The subscription is void and a fresh SUBSCRIBE should follow.</summary>
        Resubscribe,

        /// <summary>The subscription is void and the stream has ended.</summary>
        Ended
    }

    /// <summary>
    /// Acts on a pending presence cancellation exactly once. Called wherever the attempt
    /// loop can move on, so a notice arriving during a retry backoff is not stranded.
    /// Recovery is a fresh SUBSCRIBE with NT + CALLBACK (clause 4.1.2), never a renewal:
    /// the old SID no longer exists on the device.
    /// </summary>
    private CancellationOutcome ConsumeCancellation()
    {
        if (Interlocked.Exchange(ref _cancelled, null) is not { } cancelled)
        {
            return CancellationOutcome.None;
        }

        var willResubscribe = cancelled.MayResubscribe && _options.AutoResubscribe;

        Emit(new SubscriptionCancelled(cancelled.Reason, willResubscribe));

        if (willResubscribe)
        {
            return CancellationOutcome.Resubscribe;
        }

        Error(new UpnpException(cancelled.Reason));
        return CancellationOutcome.Ended;
    }

    /// <summary>
    /// The two events UDA 2.0 clause 4.1.1 names as cancelling a subscription: the
    /// publisher withdrawing its advertisements, and a boot-identity change it did not
    /// announce in advance. <see cref="DeviceUpdated"/> is deliberately absent - a
    /// description that changed under a device that never restarted leaves the
    /// subscription intact - and so is the announced-config-change path, which the
    /// roster resolves to no change at all.
    /// </summary>
    private void OnPresenceChange(RosterChange change)
    {
        if (!string.Equals(change.Device.Usn?.DeviceUUID, _identity.Uuid, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Cancellation? cancellation = change switch
        {
            DeviceLeft => new Cancellation(
                $"The device {_identity.Udn} withdrew its advertisements, so this subscription is cancelled " +
                "(UDA 2.0 clause 4.1.1).",
                MayResubscribe: false),

            // Clause 4.1.2 requires subscribing to the eventSubURL the device's
            // description advertises. Clause 1.2.2 makes an unchanged CONFIGID a
            // guarantee that the description - and so that URL - is unchanged; without
            // that guarantee the cached URL may no longer be the right one, and
            // re-describing is the consumer's move, not ours.
            DeviceRebooted rebooted => new Cancellation(
                $"The device {_identity.Udn} restarted without announcing it, so this " +
                "subscription is cancelled (UDA 2.0 clause 4.1.1).",
                MayResubscribe: _identity.ConfigId is { } configId && rebooted.Device.ConfigId == configId),

            _ => null
        };

        if (cancellation is null)
        {
            return;
        }

        // No gate here, deliberately: this runs while the ROSTER holds its own gate
        // (it emits under it), and taking this source's gate as well would close a
        // deadlock cycle against the engine start, which holds this gate and reaches
        // for the roster's. Both fields are single-writer-per-transition and published
        // with volatile/interlocked accesses instead.
        Volatile.Write(ref _cancelled, cancellation);

        var attempt = Volatile.Read(ref _attempt);

        // Cancelling runs engine continuations inline; nothing is held here.
        try
        {
            attempt?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The attempt already ended; the flag is enough.
        }
    }

    /// <summary>Per-attempt SEQ expectation; a class so the NOTIFY closure can mutate it.</summary>
    private sealed class SeqTracker
    {
        public uint Expected;
    }

    private void HandleNotify(NotifyRequest notify, SeqTracker seqTracker, CancellationTokenSource resubscribe)
    {
        var parsed = GenaParser.ParsePropertySet(notify.Body);

        if (!parsed.IsSuccess)
        {
            _logger.UnparsableNotify(parsed.Error);
            return;
        }

        var seq = notify.Seq;
        var triggerResubscribe = false;

        // One gate for the whole message (review RX-1): the SEQ expectation,
        // the gap emission and the property batch move together, so NOTIFYs
        // delivered concurrently cannot race the expectation into a false gap
        // or interleave their batches. Compliant devices await our 200 before
        // the next NOTIFY, but the engine's invariants must not depend on
        // device politeness.
        lock (Gate)
        {
            if (seq is { } actual)
            {
                if (actual != seqTracker.Expected)
                {
                    EmitLocked(new GapDetected(seqTracker.Expected, actual));
                    triggerResubscribe = _options.AutoResubscribe;
                }

                if (!triggerResubscribe)
                {
                    // UDA 2.0 §4.2.3: the event key wraps from uint.MaxValue to
                    // 1 - 0 only ever marks a subscription's initial state.
                    seqTracker.Expected = actual == uint.MaxValue ? 1 : actual + 1;
                }
            }

            if (!triggerResubscribe)
            {
                var isInitial = seq is 0;

                foreach (var property in parsed.Value)
                {
                    var change = new PropertyChange(property.Name, property.Value, seq ?? 0, isInitial, IsReplay: false);
                    _lastKnown[change.Name] = change;
                    EmitLocked(change);
                }
            }
        }

        if (triggerResubscribe)
        {
            // Outside the gate: cancelling runs engine continuations inline.
            try
            {
                resubscribe.Cancel();            // fresh SUBSCRIBE → fresh SEQ 0 state
            }
            catch (ObjectDisposedException)
            {
                // The attempt already ended; nothing to trigger.
            }
        }
    }
}
