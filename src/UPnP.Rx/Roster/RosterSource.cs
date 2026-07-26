using System.Reactive;
using System.Reactive.Linq;
using Microsoft.Extensions.Logging;

namespace UPnP.Rx.Roster;

/// <summary>
/// The shared device-roster engine: one per <see cref="UpnpClient"/>, started by
/// the first subscriber, stopped by the last disposal (a restart begins from a
/// clean slate and a fresh M-SEARCH). Reuses the eventing engine's proven
/// shape: a reentrant gate serializes every emission and guards the per-key
/// state that late subscribers receive as replay.
/// </summary>
internal sealed class RosterSource : EngineSource<RosterChange>
{
    /// <summary>How often expiry deadlines are checked; coarse is fine - advertisement lifetimes are minutes.</summary>
    private static readonly TimeSpan _sweepPeriod = TimeSpan.FromSeconds(1);

    private readonly UpnpClient _client;
    private readonly UpnpClientOptions _options;
    private readonly ILogger _logger;

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _healing = new(StringComparer.OrdinalIgnoreCase);

    internal RosterSource(UpnpClient client, UpnpClientOptions options, CancellationToken clientLifetime)
        : base(clientLifetime)
    {
        _client = client;
        _options = options;
        _logger = options.Logger;
    }

    private sealed class Entry
    {
        public required DiscoveredDevice Device { get; set; }
        public required long SeenAt { get; set; }
        public required TimeSpan MaxAge { get; set; }
    }

    /// <summary>A previous run's roster is stale; a restart begins from a clean slate.</summary>
    protected override void ClearStateLocked() => _entries.Clear();

    /// <summary>Late subscribers receive the current roster, flagged as replay.</summary>
    protected override void ReplayLocked(IObserver<RosterChange> observer)
    {
        foreach (var entry in _entries.Values)
        {
            observer.OnNext(new DeviceAppeared(entry.Device, IsReplay: true));
        }
    }

    protected override async Task RunEngineAsync(CancellationToken ct)
    {
        try
        {
            // Announcement handling is async (self-heal does network I/O) and
            // stays in the pipeline; one bad announcement never kills the run.
            using var announcements = _client.RosterAnnouncements()
                .SelectMany(item => Observable
                    .FromAsync(innerCt => HandleAnnouncementAsync(item.Device, item.MaxAge, innerCt))
                    .Catch((Exception e) =>
                    {
                        _logger.LogDebug(e, "Handling an announcement for the roster failed.");
                        return Observable.Empty<Unit>();
                    }))
                .Subscribe(
                    _ => { },
                    e => _logger.LogError(e, "The roster's announcement stream terminated."));

            using var farewells = _client.DeviceLost()
                .Subscribe(
                    HandleByeBye,
                    e => _logger.LogError(e, "The roster's byebye stream terminated."));

            try
            {
                await _client.SearchAsync(ct: ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // The roster still fills from unsolicited announcements.
                _logger.LogWarning(e, "The roster's opening M-SEARCH failed.");
            }

            using var timer = new PeriodicTimer(_sweepPeriod, _options.TimeProvider);

            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                Sweep();
            }
        }
        catch (OperationCanceledException)
        {
            // Last subscriber left, or the client is going away.
        }
        catch (Exception e)
        {
            // The one legitimate OnError: the engine itself died (Rx rule 6).
            _logger.LogError(e, "The roster engine failed.");
            Error(new UpnpException($"The device roster failed unexpectedly: {e.Message}", e));
        }
    }

    private async Task HandleAnnouncementAsync(DiscoveredDevice device, TimeSpan maxAge, CancellationToken ct)
    {
        if (device.Location is null)
        {
            return;
        }

        var key = device.Usn?.DeviceUUID is { Length: > 0 } uuid ? uuid : device.Location.ToString();
        var effectiveMaxAge = maxAge > TimeSpan.Zero ? maxAge : _options.RosterExpiryFallback;
        var knownAndUnchanged = false;

        lock (Gate)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                var rebooted = device.BootId != entry.Device.BootId;
                entry.Device = device;
                entry.SeenAt = _options.TimeProvider.GetTimestamp();
                entry.MaxAge = effectiveMaxAge;

                if (rebooted)
                {
                    EmitLocked(new DeviceUpdated(device));
                }
                else
                {
                    knownAndUnchanged = true;
                }
            }
            else
            {
                _entries[key] = new Entry
                {
                    Device = device,
                    SeenAt = _options.TimeProvider.GetTimestamp(),
                    MaxAge = effectiveMaxAge
                };
                EmitLocked(new DeviceAppeared(device, IsReplay: false));
            }
        }

        if (!knownAndUnchanged)
        {
            return;
        }

        // Q2 self-healing, the cheap way: only when someone described this
        // device before AND the cached description's TTL has lapsed (the fetch
        // would happen on next access anyway) - piggybacking on traffic that
        // already happened, no proactive timers.
        var (expired, previousHash) = _client.DescriptionCacheState(device.Location);

        if (!expired || previousHash is null)
        {
            return;
        }

        lock (Gate)
        {
            // Announcements are handled concurrently; one heal per key at a
            // time, or a racing pair could double-emit DeviceUpdated (the same
            // don't-trust-device-politeness reasoning as the eventing RX-1 fix).
            if (!_healing.Add(key))
            {
                return;
            }
        }

        try
        {
            var described = await device.GetDescriptionAsync(ct).ConfigureAwait(false);

            if (!string.Equals(described.ContentHash, previousHash, StringComparison.Ordinal))
            {
                Emit(new DeviceUpdated(device));
            }
        }
        catch (UpnpException e)
        {
            // The failed fetch already evicted itself from the cache; the next
            // announcement retries. Presence is unaffected.
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(e, "Roster re-describe of {Location} failed.", device.Location);
            }
        }
        finally
        {
            lock (Gate)
            {
                _healing.Remove(key);
            }
        }
    }

    /// <summary>Byebyes carry no location; only USN-keyed entries can say goodbye (location-keyed ones expire).</summary>
    private void HandleByeBye(DiscoveredDevice device)
    {
        if (device.Usn?.DeviceUUID is not { Length: > 0 } uuid)
        {
            return;
        }

        lock (Gate)
        {
            if (_entries.Remove(uuid, out var entry))
            {
                EmitLocked(new DeviceLeft(entry.Device));
            }
        }
    }

    private void Sweep()
    {
        lock (Gate)
        {
            List<string>? lapsed = null;

            foreach (var (key, entry) in _entries)
            {
                if (_options.TimeProvider.GetElapsedTime(entry.SeenAt) > entry.MaxAge)
                {
                    (lapsed ??= []).Add(key);
                }
            }

            if (lapsed is null)
            {
                return;
            }

            foreach (var key in lapsed)
            {
                _entries.Remove(key, out var entry);
                EmitLocked(new DeviceExpired(entry!.Device));
            }
        }
    }
}
