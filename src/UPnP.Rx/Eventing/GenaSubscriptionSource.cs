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

    private readonly object _gate = new();
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
            // Q5: late subscribers get the last-known state first, flagged as
            // replay - under the same gate that live emissions use, so there is
            // no window for a missed or duplicated change.
            foreach (var change in _lastKnown.Values)
            {
                observer.OnNext(change with { IsReplay = true });
            }

            _observers.Add(observer);

            if (_observers.Count == 1)
            {
                _lastKnown.Clear();          // stale state from a previous run
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

    private async Task RunEngineAsync(CancellationToken ct)
    {
        var attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            attempt++;

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
                catch (Exception e) when (e is UpnpException && !ct.IsCancellationRequested)
                {
                    Emit(new RenewalFailed($"SUBSCRIBE failed: {e.Message}"));

                    if (!_options.AutoResubscribe)
                    {
                        Error(new UpnpException($"The event subscription to {_eventSubUrl} could not be established: {e.Message}", e));
                        return;
                    }

                    await Task.Delay(_retryDelay, _options.TimeProvider, ct).ConfigureAwait(false);
                    continue;
                }

                Emit(attempt == 1 ? new Subscribed(sid, granted) : new Resubscribed(sid));

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
                    catch (Exception e) when (e is UpnpException)
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

    private static readonly TimeSpan _retryDelay = TimeSpan.FromSeconds(10);

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

            seqTracker.Expected = actual + 1;
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
