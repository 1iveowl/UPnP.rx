using System.Collections.Concurrent;

namespace UPnP.Rx;

/// <summary>
/// The description cache, extracted from <see cref="UpnpClient"/> (structural
/// review, 4.2). Cached by LOCATION + CONFIGID + BOOTID: CONFIGID is UDA 2.0's
/// "the description changed" signal - but the UPnP 1.0 installed base (most
/// real devices) never sends it, which would make the first read immortal: one
/// sparse description served mid-boot (seen on Sonos) would stick for the
/// client's lifetime. BOOTID makes a reboot re-read the device; the
/// announcement's CACHE-CONTROL max-age additionally expires entries WITHIN a
/// boot, so a bad read heals by the next advertisement cycle. Entries without
/// a max-age never expire. A fresh generation evicts superseded ones (a flappy
/// device stays at one entry), and only successful fetches stay cached - a
/// transient failure must not poison the device forever.
/// </summary>
internal sealed class DescriptionCache(TimeProvider timeProvider)
{
    private sealed record Entry(Lazy<Task<DescribedDevice>> Described, long Created, TimeSpan MaxAge);

    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    /// <summary>The entry count - test seam for boundedness assertions.</summary>
    internal int Count => _entries.Count;

    internal Task<DescribedDevice> GetOrFetchAsync(
        Uri location, int? configId, uint bootId, TimeSpan maxAge,
        Func<Task<DescribedDevice>> fetch, CancellationToken ct)
    {
        var key = $"{location}#{configId}#{bootId}";

        while (true)
        {
            var entry = _entries.GetOrAdd(
                key,
                _ =>
                {
                    EvictOtherGenerations(location, key);

                    return new Entry(
                        new Lazy<Task<DescribedDevice>>(() => FetchAndEvictOnFailureAsync(key, fetch)),
                        timeProvider.GetTimestamp(),
                        maxAge);
                });

            if (entry.MaxAge > TimeSpan.Zero
                && timeProvider.GetElapsedTime(entry.Created) > entry.MaxAge)
            {
                // Expired: remove exactly this entry (benign race with others) and retry.
                _entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
                continue;
            }

            return entry.Described.Value.WaitAsync(ct);
        }
    }

    /// <summary>Drops every generation cached for <paramref name="location"/> - the manual escape hatch.</summary>
    internal void Invalidate(Uri location)
    {
        var prefix = $"{location}#";

        foreach (var key in _entries.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)))
        {
            _entries.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// What the cache knows about <paramref name="location"/>: whether the
    /// newest entry's TTL lapsed, and the content hash when its fetch
    /// completed. (false, null) when never described - the roster only
    /// self-heals devices a consumer actually described.
    /// </summary>
    internal (bool Expired, string? Hash) State(Uri location)
    {
        var prefix = $"{location}#";
        Entry? newest = null;

        foreach (var (key, entry) in _entries)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal)
                && (newest is null || entry.Created > newest.Created))
            {
                newest = entry;
            }
        }

        if (newest is null)
        {
            return (false, null);
        }

        var expired = newest.MaxAge > TimeSpan.Zero
            && timeProvider.GetElapsedTime(newest.Created) > newest.MaxAge;
        // Guarded .Result: the task is known completed - no blocking (rule 3).
        var hash = newest.Described.IsValueCreated && newest.Described.Value.IsCompletedSuccessfully
            ? newest.Described.Value.Result.ContentHash
            : null;

        return (expired, hash);
    }

    /// <summary>A fresh generation supersedes older boots/configs of the same device.</summary>
    private void EvictOtherGenerations(Uri location, string keepKey)
    {
        var prefix = $"{location}#";

        foreach (var existing in _entries.Keys)
        {
            if (existing.StartsWith(prefix, StringComparison.Ordinal)
                && !string.Equals(existing, keepKey, StringComparison.Ordinal))
            {
                _entries.TryRemove(existing, out _);
            }
        }
    }

    private async Task<DescribedDevice> FetchAndEvictOnFailureAsync(string key, Func<Task<DescribedDevice>> fetch)
    {
        try
        {
            return await fetch().ConfigureAwait(false);
        }
        catch
        {
            _entries.TryRemove(key, out _);
            throw;
        }
    }
}
