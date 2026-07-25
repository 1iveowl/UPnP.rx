using System.Reactive.Disposables;
using Microsoft.Extensions.Logging;

namespace UPnP.Rx.Eventing;

/// <summary>
/// One shared GENA subscription per service: N Rx subscribers, one SUBSCRIBE on
/// the device. The Rx subscription IS the GENA subscription - the first
/// subscriber starts the engine, the last disposal stops it (UNSUBSCRIBE runs
/// in the engine task's teardown, never fire-and-forget from Dispose). A gate
/// serializes every emission and guards the last-known-value snapshot that late
/// subscribers receive as replay (plan decisions Q1/Q2/Q5).
/// </summary>
internal sealed class GenaSubscriptionSource : IObservable<UpnpEvent>
{
    private readonly Uri _eventSubUrl;
    private readonly Func<string, Uri> _callbackForToken;
    private readonly IGenaTransport _transport;
    private readonly Func<string, Func<NotifyRequest, CancellationToken, Task>, IDisposable> _registerRoute;
    private readonly UpnpClientOptions _options;
    private readonly ILogger _logger;
    private readonly CancellationToken _clientLifetime;

    private static readonly TimeSpan _retryDelay = TimeSpan.FromSeconds(10);

    private readonly Lock _gate = new();
    private readonly List<IObserver<UpnpEvent>> _observers = [];
    private readonly Dictionary<string, PropertyChange> _lastKnown = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _engineCts;
    private Task _engineTask = Task.CompletedTask;

    internal GenaSubscriptionSource(
        Uri eventSubUrl,
        Func<string, Uri> callbackForToken,
        IGenaTransport transport,
        Func<string, Func<NotifyRequest, CancellationToken, Task>, IDisposable> registerRoute,
        UpnpClientOptions options,
        ILogger logger,
        CancellationToken clientLifetime)
    {
        _eventSubUrl = eventSubUrl;
        _callbackForToken = callbackForToken;
        _transport = transport;
        _registerRoute = registerRoute;
        _options = options;
        _logger = logger;
        _clientLifetime = clientLifetime;
    }

    public IDisposable Subscribe(IObserver<UpnpEvent> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);

        lock (_gate)
        {
            var isFirst = _observers.Count == 0;

            if (isFirst)
            {
                // A fresh run: anything remembered is from before the engine
                // stopped - stale by definition, and about to be superseded by
                // the new subscription's SEQ 0 initial state.
                _lastKnown.Clear();
            }
            else
            {
                // Q5: late subscribers get the last-known state first, flagged
                // as replay - under the same gate that live emissions use, so
                // there is no window for a missed or duplicated change.
                foreach (var change in _lastKnown.Values)
                {
                    observer.OnNext(change with { IsReplay = true });
                }
            }

            _observers.Add(observer);

            if (isFirst)
            {
                _engineCts?.Dispose();       // an OnError'd run leaves its CTS behind
                _engineCts = CancellationTokenSource.CreateLinkedTokenSource(_clientLifetime);
                _engineTask = RunEngineAsync(_engineCts.Token);
            }
        }

        return Disposable.Create(() =>
        {
            lock (_gate)
            {
                _observers.Remove(observer);

                if (_observers.Count == 0 && _engineCts is not null)
                {
                    // The engine task observes this and runs its own teardown
                    // (UNSUBSCRIBE) inside the task - disposal-model rule 3.
                    _engineCts.Cancel();
                    _engineCts.Dispose();
                    _engineCts = null;
                }
            }
        });
    }

    /// <summary>
    /// Graceful stop for client disposal: cancels the engine and returns its
    /// task, so callers can await the in-task teardown (UNSUBSCRIBE included -
    /// it is bounded by <see cref="UpnpClientOptions.ActionTimeout"/>).
    /// Remaining observers are completed; the stream ends without error.
    /// </summary>
    internal Task ShutdownAsync()
    {
        lock (_gate)
        {
            if (_engineCts is not null)
            {
                _engineCts.Cancel();
                _engineCts.Dispose();
                _engineCts = null;
            }

            foreach (var observer in _observers.ToArray())
            {
                observer.OnCompleted();
            }

            _observers.Clear();
            return _engineTask;
        }
    }

    private async Task RunEngineAsync(CancellationToken ct)
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
            _logger.LogError(e, "The event subscription engine for {Url} failed.", _eventSubUrl);
            Error(new UpnpException($"The event subscription engine for {_eventSubUrl} failed unexpectedly: {e.Message}", e));
        }
    }

    private async Task RunAttemptsAsync(CancellationToken ct)
    {
        var everSubscribed = false;

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

                    await Task.Delay(_retryDelay, _options.TimeProvider, ct).ConfigureAwait(false);
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
                // Teardown runs INSIDE the engine task: say goodbye when we had
                // a live subscription and the device is presumably still there.
                if (sid is not null)
                {
                    try
                    {
                        using var goodbye = new CancellationTokenSource(_options.ActionTimeout, _options.TimeProvider);
                        await _transport.UnsubscribeAsync(_eventSubUrl, sid, goodbye.Token).ConfigureAwait(false);
                    }
                    catch (Exception e)
                    {
                        _logger.LogDebug(e, "UNSUBSCRIBE for {Sid} failed; the device will time the subscription out.", sid);
                    }
                }
            }
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
            _logger.LogDebug("Dropped an unparsable NOTIFY: {Error}", parsed.Error);
            return;
        }

        var seq = notify.Seq;

        if (seq is { } actual)
        {
            if (actual != seqTracker.Expected)
            {
                Emit(new GapDetected(seqTracker.Expected, actual));

                if (_options.AutoResubscribe)
                {
                    try
                    {
                        resubscribe.Cancel();    // fresh SUBSCRIBE → fresh SEQ 0 state
                    }
                    catch (ObjectDisposedException)
                    {
                        // The attempt already ended; nothing to trigger.
                    }

                    return;
                }
            }

            // UDA 2.0 §4.2.3: the event key wraps from uint.MaxValue to 1 - 0
            // only ever marks a subscription's initial state.
            seqTracker.Expected = actual == uint.MaxValue ? 1 : actual + 1;
        }

        var isInitial = seq is 0;

        lock (_gate)
        {
            foreach (var property in parsed.Value)
            {
                var change = new PropertyChange(property.Name, property.Value, seq ?? 0, isInitial, IsReplay: false);
                _lastKnown[change.Name] = change;

                foreach (var observer in _observers.ToArray())
                {
                    observer.OnNext(change);
                }
            }
        }
    }

    private void Emit(UpnpEvent value)
    {
        lock (_gate)
        {
            foreach (var observer in _observers.ToArray())
            {
                observer.OnNext(value);
            }
        }
    }

    private void Error(Exception error)
    {
        lock (_gate)
        {
            foreach (var observer in _observers.ToArray())
            {
                observer.OnError(error);
            }

            _observers.Clear();
        }
    }
}
