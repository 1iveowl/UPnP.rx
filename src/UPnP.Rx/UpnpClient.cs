using System.Collections.Concurrent;
using System.Net;
using System.Reactive.Linq;
using Microsoft.Extensions.Logging;
using SSDP.UPnP.PCL;
using SSDP.UPnP.PCL.Model;
using UPnP.Rx.Parsing;

namespace UPnP.Rx;

/// <summary>
/// The UPnP control point client: discover devices (SSDP via SSDP.UPnP.PCL),
/// fetch their descriptions lazily with caching, and control their services.
/// </summary>
/// <remarks>
/// Construction is side-effect free; the underlying SSDP control point starts on
/// the first subscription to <see cref="DiscoverDevices"/> or
/// <see cref="DeviceLost"/>. Disposal follows the house disposal model:
/// <see cref="DisposeAsync"/> and <see cref="Dispose"/> both stop discovery and
/// release owned resources (v1 owes the network no goodbye; eventing in v2 will
/// make <see cref="DisposeAsync"/> the graceful path).
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
    private readonly ConcurrentDictionary<string, Lazy<Task<DescribedDevice>>> _descriptions = new();
    private readonly Lock _startLock = new();
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
    }

    /// <summary>
    /// Advanced: creates a client over an existing SSDP control point and/or
    /// <see cref="HttpClient"/>. The caller keeps ownership of both — disposing
    /// this client will not dispose them.
    /// </summary>
    /// <param name="controlPoint">The SSDP control point; may already be started (e.g. via <c>HotStart</c>).</param>
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
    }

    /// <summary>
    /// Devices announcing themselves: M-SEARCH responses (a search is sent on
    /// subscription) merged with <c>ssdp:alive</c> notifications, deduplicated per
    /// subscription by USN + boot instance.
    /// </summary>
    /// <param name="searchTarget">The search target; the options' <see cref="UpnpClientOptions.DefaultSearchTarget"/> when null.</param>
    /// <param name="mx">Maximum device response delay; the options' <see cref="UpnpClientOptions.DefaultMx"/> when null.</param>
    /// <remarks>
    /// Temperature: cold — each subscription starts the control point if needed,
    /// sends a fresh M-SEARCH and observes the shared SSDP streams. The stream
    /// stays open (devices keep announcing); dispose the subscription to stop.
    /// Degraded announcements are kept (<see cref="DiscoveredDevice.HasParsingError"/>);
    /// only messages without a usable <c>LOCATION</c> are dropped, with a log note.
    /// Deduplication state grows with the number of distinct device×boot
    /// identities seen — on very long-lived subscriptions, resubscribe
    /// periodically (a live roster with expiry is planned for v1.1).
    /// </remarks>
    public IObservable<DiscoveredDevice> DiscoverDevices(ST? searchTarget = null, TimeSpan? mx = null) =>
        Observable.Defer(() =>
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            EnsureStarted();

            var discovered = _controlPoint
                .MSearchResponseObservable()
                .Select(response => ToDiscovered(
                    response.USN, response.Location, response.Server, response.BOOTID,
                    response.CONFIGID, response.HasParsingError, response.LocalIpEndPoint))
                .Merge(_controlPoint
                    .NotifyObservable()
                    .Where(notify => notify.NTS == NTS.Alive)
                    .Select(notify => ToDiscovered(
                        notify.USN, notify.Location, notify.Server, notify.BOOTID,
                        notify.CONFIGID, notify.HasParsingError, notify.LocalIpEndPoint)))
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
    /// <remarks>Temperature: cold subscription over the shared SSDP notify stream; starts the control point if needed.</remarks>
    public IObservable<DiscoveredDevice> DeviceLost() =>
        Observable.Defer(() =>
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            EnsureStarted();

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
    /// Stops discovery and releases owned resources. Equivalent to
    /// <see cref="Dispose"/> in v1 — a control point owes the network no goodbye;
    /// v2 eventing will unsubscribe here.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private void EnsureStarted()
    {
        lock (_startLock)
        {
            if (!_controlPoint.IsStarted)
            {
                _controlPoint.Start(_lifetime.Token);
            }
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

    private DiscoveredDevice? ToDiscovered(
        USN? usn, Uri? location, Server? server, uint bootId, int? configId, bool hasParsingError,
        IPEndPoint? localEndPoint)
    {
        if (location is null)
        {
            _options.Logger.LogDebug("Dropped an announcement without a usable LOCATION (USN: {Usn}).", usn);
            return null;
        }

        return new DiscoveredDevice(
            usn, location, server, bootId, configId, hasParsingError, localEndPoint,
            ct => GetOrFetchDescriptionAsync(location, configId, bootId, ct));
    }

    private Task<DescribedDevice> GetOrFetchDescriptionAsync(Uri location, int? configId, uint bootId, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        // Cached by LOCATION + CONFIGID + BOOTID. CONFIGID is UDA 2.0's "the
        // description changed" signal - but the UPnP 1.0 installed base (most
        // real devices) never sends it, which would make the first read
        // immortal: one sparse description served mid-boot (seen on Sonos)
        // would stick for the client's lifetime. BOOTID in the key makes a
        // reboot re-read the device naturally.
        var key = $"{location}#{configId}#{bootId}";
        var entry = _descriptions.GetOrAdd(
            key,
            _ => new Lazy<Task<DescribedDevice>>(() => FetchAndEvictOnFailureAsync(key, location)));

        return entry.Value.WaitAsync(ct);
    }

    /// <summary>Only successful descriptions stay cached — a transient fetch failure must not poison the device forever.</summary>
    private async Task<DescribedDevice> FetchAndEvictOnFailureAsync(string key, Uri location)
    {
        try
        {
            return await FetchDescriptionAsync(location).ConfigureAwait(false);
        }
        catch
        {
            _descriptions.TryRemove(key, out _);
            throw;
        }
    }

    private async Task<DescribedDevice> FetchDescriptionAsync(Uri location)
    {
        using var timeout = new CancellationTokenSource(_options.DescriptionTimeout, _options.TimeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, _lifetime.Token);

        string xml;

        try
        {
            xml = await _httpClient.GetStringAsync(location, linked.Token).ConfigureAwait(false);
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
            description => new DescribedDevice(description, _httpClient, _options, _lifetime.Token),
            error => throw new UpnpException($"The description at {location} is unparsable: {error}"));
    }
}
