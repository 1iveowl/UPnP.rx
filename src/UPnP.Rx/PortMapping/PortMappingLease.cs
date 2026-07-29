using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace UPnP.Rx.PortMapping;

/// <summary>
/// An auto-renewing port mapping (decision 3). A finite lease is renewed at
/// half-life on the configured <see cref="TimeProvider"/>; renewal outcomes
/// surface on <see cref="Events"/> (a failed renewal retries — it never
/// terminates the stream).
/// </summary>
/// <remarks>
/// Disposal follows the house disposal model. <see cref="DisposeAsync"/> is the
/// graceful path: stop renewing, delete the mapping from the gateway (failures
/// logged, not thrown — the router may already have dropped it). <see cref="Dispose"/>
/// is the abrupt path: stop renewing only — safe by design, because the finite
/// lease then simply expires on the router. An indefinite lease
/// (<see cref="TimeSpan.Zero"/>) opts out of both protections.
/// </remarks>
public sealed class PortMappingLease : IPortMappingLease
{
    private readonly InternetGateway _gateway;
    private readonly UpnpClientOptions _options;
    private readonly CancellationTokenSource _cts = new();
    private readonly Subject<PortMappingEvent> _subject = new();
    // Synchronized view (review RX-4): sync Dispose can complete the stream
    // while the renewal loop is mid-emission on another thread - the wrapper
    // keeps that race inside the Rx grammar.
    private readonly ISubject<PortMappingEvent> _events;
    private readonly Task _renewalLoop;
    private InternetGateway? _ownedGateway;
    private int _disposed;

    /// <summary>Makes this lease own (and dispose) the whole discovery chain — used by the PortMapper one-liner.</summary>
    internal PortMappingLease AttachOwnedGateway(InternetGateway gateway)
    {
        _ownedGateway = gateway;
        return this;
    }

    internal PortMappingLease(InternetGateway gateway, PortMappingEntry mapping, UpnpClientOptions options)
    {
        _gateway = gateway;
        Mapping = mapping;
        _options = options;
        _events = Subject.Synchronize(_subject);
        // A finite lease is the only one worth renewing. Indefinite (zero) needs no
        // renewal by definition, and an unknown lease (null - only reachable for a
        // mapping read back from a gateway that reported none) gives no half-life to
        // renew at, so both opt out.
        _renewalLoop = mapping.LeaseDuration is { } finite && finite > TimeSpan.Zero
            ? RunRenewalLoopAsync(finite)
            : Task.CompletedTask;
    }

    /// <summary>The mapping as granted (<c>AddAnyPortMapping</c> may have shifted the external port).</summary>
    public PortMappingEntry Mapping { get; }

    /// <summary>
    /// Renewal-lifecycle notifications: <see cref="PortMappingEventKind.Renewed"/>,
    /// <see cref="PortMappingEventKind.RenewalFailed"/> (retrying),
    /// <see cref="PortMappingEventKind.Expired"/>. Hot; completes on disposal.
    /// </summary>
    public IObservable<PortMappingEvent> Events => _events.AsObservable();

    /// <summary>Graceful: stop renewing and delete the mapping from the gateway (delete failures are logged, not thrown). Idempotent and thread-safe.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        await _renewalLoop.ConfigureAwait(false);

        try
        {
            await _gateway.DeletePortMappingAsync(Mapping.ExternalPort, Mapping.Protocol).ConfigureAwait(false);
        }
        catch (Exception e) when (e is UpnpException or OperationCanceledException)
        {
            _options.Logger.PortMappingDeleteOnDisposeFailed(e, Mapping.ExternalPort, Mapping.Protocol);
        }

        _events.OnCompleted();
        _subject.Dispose();
        _cts.Dispose();

        if (_ownedGateway is not null)
        {
            await _ownedGateway.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Abrupt: stop renewing only — no network goodbye (disposal model rule 4).
    /// The finite lease expires on the router by itself. Prefer <c>await using</c>.
    /// Idempotent and thread-safe.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cts.Cancel();
        // The renewal loop may still be mid-iteration: completing the subject is
        // safe (post-terminal OnNext is a no-op), but disposing it — or the CTS —
        // here would race the loop into ObjectDisposedException on an unobserved
        // task. The abrupt path leaves both for the loop's end / the GC.
        _events.OnCompleted();
        _ownedGateway?.Dispose();
    }

    private async Task RunRenewalLoopAsync(TimeSpan lease)
    {
        // Renew at half-life; never spin faster than once per second.
        var period = TimeSpan.FromTicks(Math.Max(lease.Ticks / 2, TimeSpan.TicksPerSecond));

        var timeProvider = _options.TimeProvider;
        using var timer = new PeriodicTimer(period, timeProvider);
        var lastSuccess = timeProvider.GetTimestamp();
        var expiredEmitted = false;

        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
            {
                try
                {
                    await _gateway.RenewAsync(Mapping, _cts.Token).ConfigureAwait(false);

                    lastSuccess = timeProvider.GetTimestamp();
                    expiredEmitted = false;
                    _events.OnNext(new PortMappingEvent { Kind = PortMappingEventKind.Renewed });
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    // Our own disposal — exit quietly. External cancellation (e.g.
                    // the owning UpnpClient was disposed under a live lease) falls
                    // through to the failure branch below so it stays VISIBLE:
                    // renewals failing silently is how port forwards die unnoticed.
                    throw;
                }
                catch (Exception e)
                {
                    // Per-item failure is data: report, keep retrying.
                    _events.OnNext(new PortMappingEvent
                    {
                        Kind = PortMappingEventKind.RenewalFailed,
                        Message = e.Message
                    });

                    if (!expiredEmitted
                        && timeProvider.GetElapsedTime(lastSuccess) > lease)
                    {
                        expiredEmitted = true;
                        _events.OnNext(new PortMappingEvent { Kind = PortMappingEventKind.Expired });
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disposal: the loop simply ends.
        }
    }
}
