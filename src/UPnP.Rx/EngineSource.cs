using System.Reactive.Disposables;

namespace UPnP.Rx;

/// <summary>
/// The shared shape of the library's engine-backed observables (the eventing
/// and roster sources): the first subscriber starts the engine, the last
/// disposal cancels it, a reentrant gate serializes every emission and guards
/// the per-key state late subscribers receive as replay, and a subscriber
/// arriving after the owning client's lifetime ended is completed immediately
/// instead of going silent. Extracted once both engines had proven the
/// pattern independently (dedup review, 4.1.1).
/// </summary>
internal abstract class EngineSource<TEvent> : IObservable<TEvent>
{
    private readonly CancellationToken _clientLifetime;
    private readonly List<IObserver<TEvent>> _observers = [];
    private CancellationTokenSource? _engineCts;
    private Task _engineTask = Task.CompletedTask;

    // Load-bearing: System.Threading.Lock is reentrant (verified on net10.0).
    // Engines depend on it - awaits of already-completed tasks continue
    // inline, so emissions can re-enter the gate on the thread that already
    // holds it (e.g. while Subscribe starts the engine). A non-reentrant
    // primitive here would deadlock.
    protected Lock Gate { get; } = new();

    protected EngineSource(CancellationToken clientLifetime) => _clientLifetime = clientLifetime;

    public IDisposable Subscribe(IObserver<TEvent> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);

        lock (Gate)
        {
            if (_clientLifetime.IsCancellationRequested)
            {
                // The owning client is gone - complete instead of going silent.
                observer.OnCompleted();
                return Disposable.Empty;
            }

            if (_observers.Count == 0)
            {
                // A fresh run: state remembered from before the engine stopped
                // is stale by definition.
                ClearStateLocked();
            }
            else
            {
                // Late subscribers get the current state first, flagged as
                // replay - under the same gate live emissions use, so there is
                // no window for a missed or duplicated change.
                ReplayLocked(observer);
            }

            _observers.Add(observer);

            if (_observers.Count == 1)
            {
                _engineCts?.Dispose();       // an OnError'd run leaves its CTS behind
                _engineCts = CancellationTokenSource.CreateLinkedTokenSource(_clientLifetime);
                _engineTask = RunEngineAsync(_engineCts.Token);
            }
        }

        return Disposable.Create(() =>
        {
            lock (Gate)
            {
                _observers.Remove(observer);

                if (_observers.Count == 0 && _engineCts is not null)
                {
                    // The engine observes this and runs its own teardown inside
                    // the task - disposal-model rule 3.
                    _engineCts.Cancel();
                    _engineCts.Dispose();
                    _engineCts = null;
                }
            }
        });
    }

    /// <summary>
    /// Graceful stop for client disposal: cancels the engine and returns its
    /// task, so callers can await the in-task teardown. Remaining observers
    /// are completed; the stream ends without error.
    /// </summary>
    internal Task ShutdownAsync()
    {
        lock (Gate)
        {
            if (_engineCts is not null)
            {
                _engineCts.Cancel();
                _engineCts.Dispose();
                _engineCts = null;
            }

            foreach (var observer in SnapshotObserversLocked())
            {
                observer.OnCompleted();
            }

            _observers.Clear();
            return _engineTask;
        }
    }

    /// <summary>Resets replayable state before a fresh run; the caller holds <see cref="Gate"/>.</summary>
    protected abstract void ClearStateLocked();

    /// <summary>Replays current state to a late subscriber; the caller holds <see cref="Gate"/>.</summary>
    protected abstract void ReplayLocked(IObserver<TEvent> observer);

    /// <summary>
    /// The engine, running from the first subscriber until <paramref name="ct"/>
    /// fires. Must not throw: terminal trouble goes through <see cref="Error"/>
    /// (the one legitimate OnError - Rx rule 6).
    /// </summary>
    protected abstract Task RunEngineAsync(CancellationToken ct);

    protected void Emit(TEvent value)
    {
        lock (Gate)
        {
            EmitLocked(value);
        }
    }

    /// <summary>Delivers to every observer; the caller holds <see cref="Gate"/>.</summary>
    protected void EmitLocked(TEvent value)
    {
        foreach (var observer in SnapshotObserversLocked())
        {
            observer.OnNext(value);
        }
    }

    /// <summary>Source death: errors every observer and detaches them.</summary>
    protected void Error(Exception error)
    {
        lock (Gate)
        {
            foreach (var observer in SnapshotObserversLocked())
            {
                observer.OnError(error);
            }

            _observers.Clear();
        }
    }

    /// <summary>Creates a stable observer snapshot; the caller holds <see cref="Gate"/>.</summary>
    private IObserver<TEvent>[] SnapshotObserversLocked() => [.. _observers];
}
