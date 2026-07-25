using System.Collections.Concurrent;
using System.Net;
using System.Reactive.Linq;
using Microsoft.Extensions.Logging;
using SSDP.UPnP.PCL;
using SSDP.UPnP.PCL.Model;
using UPnP.Rx.Eventing;
using UPnP.Rx.Parsing;
using UPnP.Rx.Roster;

namespace UPnP.Rx;

/// <summary>
/// The UPnP control point client: discover devices (SSDP via SSDP.UPnP.PCL),
/// fetch their descriptions lazily with caching, and control their services.
/// </summary>
/// <remarks>
/// Construction is side-effect free; since SSDP.UPnP.PCL 8.0 the underlying
/// control point is itself lazy - its sockets come up on the first subscription
/// to <see cref="DiscoverDevices"/> or <see cref="DeviceLost"/> and stop when
/// the last one is disposed (no start-once bookkeeping at any layer).
/// Disposal follows the house disposal model:
/// <see cref="DisposeAsync"/> is the graceful path (live event subscriptions say
/// UNSUBSCRIBE before resources go); <see cref="Dispose"/> is the abrupt one -
/// no network goodbyes, safe because subscriptions expire on the devices.
/// </remarks>
public sealed class UpnpClient : IAsyncDisposable, IDisposable
{
    private readonly IControlPoint _controlPoint;
    private readonly bool _ownsControlPoint;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly IReadOnlyList<IPAddress> _addresses;
    private readonly UpnpClientOptions _options;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<string, DescriptionCacheEntry> _descriptions = new();
    private readonly EventingContext _eventing;
    private RosterSource? _roster;
    private int _disposed;

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>
    /// Creates a client that owns an SSDP control point bound to the given local
    /// IPv4 addresses, with default options.
    /// </summary>
    /// <param name="addresses">One or more local IPv4 addresses to bind.</param>
    public UpnpClient(params IPAddress[] addresses)
        : this(new UpnpClientOptions(), addresses)
    {
    }

    /// <summary>
    /// Creates a client that owns an SSDP control point bound to the given local
    /// IPv4 addresses.
    /// </summary>
    /// <param name="options">The client configuration.</param>
    /// <param name="addresses">One or more local IPv4 addresses to bind.</param>
    public UpnpClient(UpnpClientOptions options, params IPAddress[] addresses)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _addresses = [.. addresses];
        _controlPoint = new ControlPoint(addresses);
        _ownsControlPoint = true;
        // All timeouts run on options.TimeProvider (time model); HttpClient's own
        // wall-clock timer must not be a hidden second clock capping them at 100 s.
        _httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _ownsHttpClient = true;
        _eventing = new EventingContext(_httpClient, _options, _lifetime.Token);
    }

    /// <summary>
    /// Advanced: creates a client over an existing SSDP control point and/or
    /// <see cref="HttpClient"/>. The caller keeps ownership of both — disposing
    /// this client will not dispose them.
    /// </summary>
    /// <param name="controlPoint">The SSDP control point; may carry an externally supplied stream (<c>HotStart</c>).</param>
    /// <param name="httpClient">The HTTP client for descriptions and SOAP; a private one is created when null.</param>
    /// <param name="options">The client configuration; defaults when null.</param>
    /// <param name="addresses">Local addresses to send M-SEARCH from; searches are skipped when empty.</param>
    public UpnpClient(
        IControlPoint controlPoint,
        HttpClient? httpClient = null,
        UpnpClientOptions? options = null,
        params IPAddress[] addresses)
    {
        ArgumentNullException.ThrowIfNull(controlPoint);

        _controlPoint = controlPoint;
        _ownsControlPoint = false;
        _httpClient = httpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _ownsHttpClient = httpClient is null;
        _options = options ?? new UpnpClientOptions();
        _addresses = [.. addresses];
        _eventing = new EventingContext(_httpClient, _options, _lifetime.Token);
    }

    /// <summary>
    /// Devices announcing themselves: M-SEARCH responses (a search is sent on
    /// subscription) merged with <c>ssdp:alive</c> notifications, deduplicated per
    /// subscription by USN + boot instance.
    /// </summary>
    /// <param name="searchTarget">The search target; the options' <see cref="UpnpClientOptions.DefaultSearchTarget"/> when null.</param>
    /// <param name="mx">Maximum device response delay; the options' <see cref="UpnpClientOptions.DefaultMx"/> when null.</param>
    /// <remarks>
    /// Temperature: cold — each subscription brings the SSDP listeners up if
    /// needed (lazily, upstream), sends a fresh M-SEARCH and observes the shared SSDP streams. The stream
    /// stays open (devices keep announcing); dispose the subscription to stop.
    /// Degraded announcements are kept (<see cref="DiscoveredDevice.HasParsingError"/>);
    /// only messages without a usable <c>LOCATION</c> are dropped, with a log note.
    /// Deduplication state grows with the number of distinct device×boot
    /// identities seen — for long-lived presence tracking prefer
    /// <see cref="Roster"/>, whose state is bounded and expiring.
    /// </remarks>
    public IObservable<DiscoveredDevice> DiscoverDevices(ST? searchTarget = null, TimeSpan? mx = null) =>
        Observable.Defer(() =>
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);

            var discovered = _controlPoint
                .MSearchResponseObservable()
                .Select(response => ToDiscovered(
                    response.USN, response.Location, response.Server, response.BOOTID,
                    response.CONFIGID, response.HasParsingError, response.LocalIpEndPoint,
                    response.CacheControl))
                .Merge(_controlPoint
                    .NotifyObservable()
                    .Where(notify => notify.NTS == NTS.Alive)
                    .Select(notify => ToDiscovered(
                        notify.USN, notify.Location, notify.Server, notify.BOOTID,
                        notify.CONFIGID, notify.HasParsingError, notify.LocalIpEndPoint,
                        notify.CacheControl)))
                .Where(device => device is not null)
                .Select(device => device!)
                .Distinct(device => $"{device.Usn?.ToUsnString() ?? device.Location!.ToString()}#{device.BootId}");

            return Observable.Create<DiscoveredDevice>(async (observer, ct) =>
            {
                // Subscribe before searching so no response outruns us.
                var subscription = discovered.Subscribe(observer);

                try
                {
                    await SendSearchesAsync(
                            searchTarget ?? _options.DefaultSearchTarget,
                            mx ?? _options.DefaultMx,
                            ct)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // If the subscribe task faults or is cancelled, Rx never
                    // receives the returned disposable — dispose it ourselves or
                    // the shared RefCount streams leak a subscriber forever.
                    subscription.Dispose();
                    throw;
                }

                return subscription;
            });
        });

    /// <summary>
    /// Devices announcing themselves, with their descriptions already fetched:
    /// <see cref="DiscoverDevices"/> composed with the description fetch, one
    /// emission per device (deduplicated by UDN). Devices whose description
    /// cannot be fetched or parsed are skipped with a log note (per-item failure
    /// is data, not stream death).
    /// </summary>
    /// <param name="searchTarget">The search target; the options' <see cref="UpnpClientOptions.DefaultSearchTarget"/> when null.</param>
    /// <param name="mx">Maximum device response delay; the options' <see cref="UpnpClientOptions.DefaultMx"/> when null.</param>
    /// <remarks>
    /// Temperature: cold — see <see cref="DiscoverDevices"/>. In-flight
    /// description fetches are cancelled when the subscription is disposed.
    /// </remarks>
    public IObservable<DescribedDevice> DiscoverDescribedDevices(ST? searchTarget = null, TimeSpan? mx = null) =>
        DiscoverDevices(searchTarget, mx)
            .SelectMany(device => Observable
                .FromAsync(device.GetDescriptionAsync)
                .Catch((UpnpException e) =>
                {
                    _options.Logger.LogDebug(e, "Skipping {Location}: description unavailable.", device.Location);
                    return Observable.Empty<DescribedDevice>();
                }))
            .Distinct(described => described.Description.Udn ?? described.Description.Location.ToString());

    /// <summary>
    /// Devices leaving the network (<c>ssdp:byebye</c>). Emitted devices carry no
    /// <see cref="DiscoveredDevice.Location"/> (byebye messages have none).
    /// </summary>
    /// <remarks>Temperature: cold subscription over the shared SSDP notify stream; the listeners come up lazily upstream.</remarks>
    public IObservable<DiscoveredDevice> DeviceLost() =>
        Observable.Defer(() =>
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);

            return _controlPoint
                .NotifyObservable()
                .Where(notify => notify.NTS == NTS.ByeBye)
                .Select(notify => new DiscoveredDevice(
                    notify.USN, notify.Location, notify.Server, notify.BOOTID, notify.CONFIGID,
                    notify.HasParsingError, notify.LocalIpEndPoint,
                    _ => Task.FromException<DescribedDevice>(
                        new UpnpException("A device-lost notice carries no description location."))));
        });

    /// <summary>Stops discovery and releases owned resources (the abrupt path of the disposal model). Idempotent and thread-safe.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        _eventing.Dispose();
        _lifetime.Dispose();

        if (_ownsControlPoint)
        {
            _controlPoint.Dispose();
        }

        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    /// <summary>
    /// The graceful path of the disposal model: live event subscriptions send
    /// UNSUBSCRIBE (each bounded by <see cref="UpnpClientOptions.ActionTimeout"/>)
    /// while the HTTP client is still alive, then discovery stops and owned
    /// resources are released. Idempotent and thread-safe.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _eventing.DisposeAsync().ConfigureAwait(false);

        _lifetime.Cancel();
        _lifetime.Dispose();

        if (_ownsControlPoint)
        {
            _controlPoint.Dispose();
        }

        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task SendSearchesAsync(ST searchTarget, TimeSpan mx, CancellationToken ct)
    {
        if (_addresses.Count == 0)
        {
            return;
        }

        var request = new MSearchRequest
        {
            ST = searchTarget,
            MX = mx,
            TransportType = TransportType.Multicast,
            CPFN = _options.ControlPointFriendlyName
        };

        var failures = 0;

        foreach (var address in _addresses)
        {
            try
            {
                await _controlPoint.SendMSearchAsync(request, address, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // One interface failing must not kill discovery on the others.
                failures++;
                _options.Logger.LogWarning(e, "M-SEARCH failed on interface {Address}.", address);
            }
        }

        if (failures == _addresses.Count)
        {
            throw new UpnpException("Sending M-SEARCH failed on every interface.");
        }
    }

    internal UpnpClientOptions Options => _options;

    /// <summary>
    /// The network's device roster as a stream of changes: arrivals, updates
    /// (reboots and healed descriptions), silent expiry per the announcements'
    /// <c>CACHE-CONTROL: max-age</c>, and byebye departures. Late subscribers
    /// first receive the current roster flagged <see cref="DeviceAppeared.IsReplay"/>,
    /// then live changes, with no gap. Unlike <see cref="DiscoverDevices"/>,
    /// state is bounded: a device is one entry however often it announces, and
    /// a byebye-then-alive cycle re-reports it even within one boot.
    /// </summary>
    /// <remarks>
    /// Temperature: cold until the first subscriber (which starts the engine
    /// and sends a fresh M-SEARCH), then hot and shared; the last disposal
    /// stops the engine, and a later resubscription starts from a clean slate.
    /// </remarks>
    public IObservable<RosterChange> Roster()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        if (_roster is null)
        {
            // Lock-free create-once: a losing duplicate is inert (nothing
            // starts until subscription) and is simply collected.
            Interlocked.CompareExchange(ref _roster, new RosterSource(this, _options, _lifetime.Token), null);
        }

        return _roster;
    }

    /// <summary>
    /// Every parsed SSDP envelope as it arrives - M-SEARCH responses,
    /// <c>ssdp:alive</c>, <c>ssdp:byebye</c> - undeduplicated and kind-tagged,
    /// timestamped on the options' <see cref="TimeProvider"/>: the device
    /// activity timeline. Deliberately passive: subscribing does <em>not</em>
    /// send an M-SEARCH (<see cref="Roster"/> and <see cref="DiscoverDevices"/>
    /// solicit; this observes), and nothing is replayed - retention is the
    /// consumer's policy. Parsed-envelope level by design; a raw wire log
    /// remains upstream territory.
    /// </summary>
    /// <remarks>Temperature: cold - each subscription observes the shared SSDP streams; the listeners come up lazily upstream.</remarks>
    public IObservable<Announcement> Announcements() =>
        Observable.Defer(() =>
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);

            return AnnouncementStream()
                .Select(item => new Announcement(
                    item.Kind, item.Device, item.MaxAge, _options.TimeProvider.GetUtcNow()));
        });

    /// <summary>The roster's raw feed: every alive/response envelope with its advertisement lifetime, undeduplicated.</summary>
    internal IObservable<(DiscoveredDevice Device, TimeSpan MaxAge)> RosterAnnouncements() =>
        AnnouncementStream()
            .Where(item => item.Kind is not AnnouncementKind.ByeBye)
            .Select(item => (item.Device, item.MaxAge));

    private IObservable<(AnnouncementKind Kind, DiscoveredDevice Device, TimeSpan MaxAge)> AnnouncementStream() =>
        _controlPoint
            .MSearchResponseObservable()
            .Select(response => (Kind: AnnouncementKind.SearchResponse, Device: ToDiscovered(
                response.USN, response.Location, response.Server, response.BOOTID,
                response.CONFIGID, response.HasParsingError, response.LocalIpEndPoint,
                response.CacheControl), MaxAge: response.CacheControl))
            .Merge(_controlPoint
                .NotifyObservable()
                .Where(notify => notify.NTS == NTS.Alive)
                .Select(notify => (Kind: AnnouncementKind.Alive, Device: ToDiscovered(
                    notify.USN, notify.Location, notify.Server, notify.BOOTID,
                    notify.CONFIGID, notify.HasParsingError, notify.LocalIpEndPoint,
                    notify.CacheControl), MaxAge: notify.CacheControl)))
            .Merge(DeviceLost()
                .Select(device => (Kind: AnnouncementKind.ByeBye, Device: (DiscoveredDevice?)device, MaxAge: TimeSpan.Zero)))
            .Where(item => item.Device is not null)
            .Select(item => (item.Kind, item.Device!, item.MaxAge));

    /// <summary>
    /// Sends one M-SEARCH burst on every configured interface without
    /// subscribing anything: a solicitation. Answers arrive on whatever
    /// streams are observing - <see cref="Roster"/>, <see cref="Announcements"/>,
    /// active <see cref="DiscoverDevices"/> subscriptions. Does nothing when
    /// the client has no addresses. (The roster engine uses this for its
    /// opening sweep; consumers use it to refresh presence or populate an
    /// activity log on demand, without the reset a re-subscription implies.)
    /// </summary>
    /// <param name="searchTarget">The search target; the options' default when null.</param>
    /// <param name="mx">Maximum device response delay; the options' default when null.</param>
    /// <param name="ct">Cancels the sends.</param>
    /// <exception cref="UpnpException">Sending failed on every interface.</exception>
    public Task SearchAsync(ST? searchTarget = null, TimeSpan? mx = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        return SendSearchesAsync(searchTarget ?? _options.DefaultSearchTarget, mx ?? _options.DefaultMx, ct);
    }

    /// <summary>
    /// What the description cache knows about <paramref name="location"/>:
    /// whether the newest entry's TTL lapsed, and the content hash when its
    /// fetch completed. (false, null) when never described - the roster only
    /// self-heals devices a consumer actually described.
    /// </summary>
    internal (bool Expired, string? Hash) DescriptionCacheState(Uri location)
    {
        var prefix = $"{location}#";
        DescriptionCacheEntry? newest = null;

        foreach (var (key, entry) in _descriptions)
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
            && _options.TimeProvider.GetElapsedTime(newest.Created) > newest.MaxAge;
        // Guarded .Result: the task is known completed - no blocking (rule 3).
        var hash = newest.Described.IsValueCreated && newest.Described.Value.IsCompletedSuccessfully
            ? newest.Described.Value.Result.ContentHash
            : null;

        return (expired, hash);
    }

    /// <summary>
    /// Drops every cached description for <paramref name="location"/>, forcing
    /// the next <see cref="DiscoveredDevice.GetDescriptionAsync"/> to re-fetch -
    /// the escape hatch for a device known to have served a stale or sparse
    /// document (they exist; see the leniency policy).
    /// </summary>
    /// <param name="location">The description URL whose cache entries to drop.</param>
    public void InvalidateDescriptions(Uri location)
    {
        ArgumentNullException.ThrowIfNull(location);

        var prefix = $"{location}#";

        foreach (var key in _descriptions.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            _descriptions.TryRemove(key, out _);
        }
    }

    private sealed record DescriptionCacheEntry(
        Lazy<Task<DescribedDevice>> Described, long Created, TimeSpan MaxAge);

    private DiscoveredDevice? ToDiscovered(
        USN? usn, Uri? location, Server? server, uint bootId, int? configId, bool hasParsingError,
        IPEndPoint? localEndPoint, TimeSpan maxAge)
    {
        if (location is null)
        {
            _options.Logger.LogDebug("Dropped an announcement without a usable LOCATION (USN: {Usn}).", usn);
            return null;
        }

        // On macOS/Linux the SSDP socket is wildcard-bound (multicast delivery
        // requires it) and the envelope reports that bound address - 0.0.0.0
        // means "unknown", not an address of ours.
        var local = localEndPoint is not null && LocalRoute.IsUsable(localEndPoint.Address)
            ? localEndPoint
            : null;

        return new DiscoveredDevice(
            usn, location, server, bootId, configId, hasParsingError, local,
            ct => GetOrFetchDescriptionAsync(location, configId, bootId, maxAge, local?.Address, ct));
    }

    private Task<DescribedDevice> GetOrFetchDescriptionAsync(
        Uri location, int? configId, uint bootId, TimeSpan maxAge, IPAddress? localAddress, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        // Cached by LOCATION + CONFIGID + BOOTID. CONFIGID is UDA 2.0's "the
        // description changed" signal - but the UPnP 1.0 installed base (most
        // real devices) never sends it, which would make the first read
        // immortal: one sparse description served mid-boot (seen on Sonos)
        // would stick for the client's lifetime. BOOTID makes a reboot re-read
        // the device; the announcement's CACHE-CONTROL max-age additionally
        // expires entries WITHIN a boot, so a bad read heals by the next
        // advertisement cycle. Entries without a max-age never expire.
        var key = $"{location}#{configId}#{bootId}";

        while (true)
        {
            var entry = _descriptions.GetOrAdd(
                key,
                _ => new DescriptionCacheEntry(
                    new Lazy<Task<DescribedDevice>>(() => FetchAndEvictOnFailureAsync(key, location, localAddress)),
                    _options.TimeProvider.GetTimestamp(),
                    maxAge));

            if (entry.MaxAge > TimeSpan.Zero
                && _options.TimeProvider.GetElapsedTime(entry.Created) > entry.MaxAge)
            {
                // Expired: remove exactly this entry (benign race with others) and retry.
                _descriptions.TryRemove(new KeyValuePair<string, DescriptionCacheEntry>(key, entry));
                continue;
            }

            return entry.Described.Value.WaitAsync(ct);
        }
    }

    /// <summary>Only successful descriptions stay cached — a transient fetch failure must not poison the device forever.</summary>
    private async Task<DescribedDevice> FetchAndEvictOnFailureAsync(string key, Uri location, IPAddress? localAddress)
    {
        try
        {
            return await FetchDescriptionAsync(location, localAddress).ConfigureAwait(false);
        }
        catch
        {
            _descriptions.TryRemove(key, out _);
            throw;
        }
    }

    private async Task<DescribedDevice> FetchDescriptionAsync(Uri location, IPAddress? localAddress)
    {
        using var timeout = new CancellationTokenSource(_options.DescriptionTimeout, _options.TimeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, _lifetime.Token);

        string xml;

        try
        {
            xml = await _httpClient.GetStringAsync(location, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            throw new ObjectDisposedException(
                nameof(UpnpClient), "The client was disposed; the description can no longer be fetched.");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new UpnpException($"Fetching the description from {location} timed out.");
        }
        catch (HttpRequestException e)
        {
            throw new UpnpException($"Fetching the description from {location} failed: {e.Message}", e);
        }

        return DescriptionParser.ParseDeviceDescription(xml, location).Match(
            description => new DescribedDevice(
                description, _httpClient, _options, _eventing, localAddress, _lifetime.Token,
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(xml)))),
            error => throw new UpnpException($"The description at {location} is unparsable: {error}"));
    }
}
