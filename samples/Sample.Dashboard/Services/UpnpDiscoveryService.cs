using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.AspNetCore.SignalR;
using Sample.Dashboard.Client.Models;
using Sample.Dashboard.Hubs;
using UPnP.Rx;
using UPnP.Rx.Presence;

namespace Sample.Dashboard.Services;

/// <summary>The server-side device roster, replayed to newly connected clients.</summary>
public sealed class DeviceRoster
{
    public ConcurrentDictionary<string, DeviceDto> Devices { get; } = new();

    /// <summary>The live library objects behind the DTOs - the hub uses them for on-demand SCPD fetches.</summary>
    public ConcurrentDictionary<string, DescribedDevice> Described { get; } = new();

    /// <summary>The discovery envelopes - the hub uses them to re-describe a device on demand.</summary>
    public ConcurrentDictionary<string, DiscoveredDevice> Discovered { get; } = new();

    /// <summary>
    /// Records a freshly described device under one key and returns its DTO. The three
    /// dictionaries have to move together - the hub previously left
    /// <see cref="Discovered"/> behind, which orphaned the old entry whenever a
    /// re-read produced a different UDN - so the write lives here rather than at each
    /// call site.
    /// </summary>
    public DeviceDto Record(DescribedDevice described, DiscoveredDevice discovered)
    {
        var dto = DtoMapper.ToDto(described, discovered);

        Devices[dto.Key] = dto;
        Described[dto.Key] = described;
        Discovered[dto.Key] = discovered;

        return dto;
    }
}

/// <summary>Maps library objects to the wire DTOs; shared by the discovery service and the hub.</summary>
internal static class DtoMapper
{
    internal static DeviceDto ToDto(DescribedDevice described, DiscoveredDevice? discovered = null)
    {
        var root = described.Description;
        var all = root.SelfAndDescendants().ToList();

        // Both witnesses that exist without extra network work: the SERVER header
        // seen at discovery, and the description's own specVersion.
        var claims = (discovered?.VersionClaims ?? UpnpVersionClaims.None).With(described.VersionClaims);

        return new DeviceDto(
            Key: NormalizeKey(root.Udn ?? root.Location.ToString()),
            FriendlyName: root.FriendlyName,
            Manufacturer: root.Manufacturer,
            Model: root.ModelName,
            Location: root.Location.ToString(),
            ServiceCount: all.SelectMany(d => d.Services).Count(),
            DeviceCount: all.Count,
            Root: ToNode(root),
            VersionClaims: [.. claims.Claims.Select(c =>
                new UpnpVersionClaimDto(c.Source.ToString(), c.Version.ToString(2), c.Detail))]);
    }

    internal static DeviceNodeDto ToNode(UPnP.Rx.Model.DeviceDescription device) => new(
        FriendlyName: device.FriendlyName,
        DeviceType: device.DeviceType,
        Manufacturer: device.Manufacturer,
        Model: device.ModelName,
        Udn: device.Udn,
        Services: [.. device.Services.Select(s => s.ServiceType).OfType<string>()],
        Children: [.. device.EmbeddedDevices.Select(ToNode)]);

    /// <summary>
    /// A lone witness's version as the browser shows it. Two digits: UDA versions are
    /// major.minor, and a trailing ".0.0" would read as precision the device never
    /// offered.
    /// </summary>
    internal static string? SoleVersion(UpnpVersionClaims claims) =>
        claims.Claims.FirstOrDefault()?.Version.ToString(2);

    internal static string NormalizeKey(string raw) =>
        raw.Trim().ToLowerInvariant() is var lower && lower.StartsWith("uuid:", StringComparison.Ordinal)
            ? lower[5..]
            : lower;
}

/// <summary>
/// Projects the library's device roster into DTOs and broadcasts changes over
/// SignalR. Since 4.1 this is a thin consumer of <see cref="UpnpClient.Roster"/> -
/// presence, expiry, reboot detection and description self-healing all live in
/// the library; this service only describes, maps and broadcasts.
/// </summary>
public sealed class UpnpDiscoveryService(
    NetworkClientProvider network,
    IHubContext<DeviceHub> hub,
    DeviceRoster roster,
    ILogger<UpnpDiscoveryService> logger) : IHostedService
{
    private readonly Subject<Unit> _rescans = new();
    private IDisposable? _rescanLoop;
    private IDisposable? _activity;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (network.Client is null)
        {
            logger.LogWarning("No usable IPv4 interfaces; the dashboard will stay empty.");
            return Task.CompletedTask;
        }

        logger.LogInformation(
            "Discovering from {Addresses}.", string.Join(", ", network.Addresses.Select(a => a.ToString())));

        // A rescan is a stream event: Switch swaps in a fresh Roster()
        // subscription - the shared roster engine stops on last-unsubscribe and
        // restarts from a clean slate with a fresh M-SEARCH, which is exactly
        // rescan semantics. Synchronize serializes requests from concurrent hub
        // connections.
        // The activity timeline runs beside the roster and survives rescans on
        // purpose - a rescan's M-SEARCH burst is exactly the traffic to show.
        _activity = network.Client.Announcements()
            .SelectMany(announcement => Observable
                .FromAsync(ct => hub.Clients.All.SendAsync(HubEvents.SsdpActivity, ToActivityDto(announcement), ct))
                .Catch((Exception e) =>
                {
                    logger.LogDebug(e, "Broadcasting an SSDP activity row failed.");
                    return Observable.Empty<Unit>();
                }))
            .Subscribe(
                _ => { },
                e => logger.LogError(e, "The SSDP activity stream terminated."));

        _rescanLoop = _rescans
            .Synchronize()
            .StartWith(Unit.Default)
            .Select(_ => RosterPipeline(network.Client))
            .Switch()
            .Subscribe(
                _ => { },
                e => logger.LogError(e, "The roster pipeline terminated."));

        return Task.CompletedTask;
    }

    /// <summary>
    /// Clears the local projection and swaps in a fresh roster subscription
    /// (fresh engine, fresh M-SEARCH). The RosterReset broadcast puts every
    /// client into rescan mode: live watches end at once, stale cards stay
    /// grayed until the first fresh device arrives.
    /// </summary>
    public async Task RescanAsync()
    {
        if (network.Client is null)
        {
            return;
        }

        roster.Devices.Clear();
        roster.Described.Clear();
        roster.Discovered.Clear();

        logger.LogInformation("Rescan: roster cleared, discovery restarted with a fresh search.");
        await hub.Clients.All.SendAsync(HubEvents.RosterReset);

        _rescans.OnNext(Unit.Default);
    }

    /// <summary>One roster generation. Async work stays in the pipeline (Rx rule 1); per-item failure is contained.</summary>
    private IObservable<Unit> RosterPipeline(UpnpClient client) =>
        client.Roster()
            .SelectMany(change => Observable
                .FromAsync(ct => HandleChangeAsync(change, ct))
                .Catch((Exception e) =>
                {
                    logger.LogDebug(e, "Skipping a roster change for {Location}.", change.Device.Location);
                    return Observable.Empty<Unit>();
                }));

    private async Task HandleChangeAsync(UPnP.Rx.Presence.RosterChange change, CancellationToken ct)
    {
        switch (change)
        {
            case UPnP.Rx.Presence.DeviceAppeared or UPnP.Rx.Presence.DeviceUpdated or UPnP.Rx.Presence.DeviceRebooted:
            {
                // Describe (the library caches; self-healed and rebooted
                // devices re-fetch automatically) and broadcast. A device whose
                // describe fails stays off the dashboard until its next roster
                // cycle (expiry + reappearance) or a manual rescan.
                var described = await change.Device.GetDescriptionAsync(ct);
                var dto = roster.Record(described, change.Device);

                await hub.Clients.All.SendAsync(HubEvents.DeviceUp, dto, ct);

                // A reboot is not an ordinary refresh: UDA 2.0 clause 1.2.4 says every
                // piece of stored state is invalid, which the user is entitled to see -
                // their live watches will have dropped at the same moment.
                if (change is UPnP.Rx.Presence.DeviceRebooted)
                {
                    await hub.Clients.All.SendAsync(HubEvents.DeviceRebooted, dto.Key, ct);
                }

                break;
            }
            case UPnP.Rx.Presence.DeviceExpired or UPnP.Rx.Presence.DeviceLeft:
            {
                if (change.Device.Usn?.DeviceUUID is not { Length: > 0 } uuid)
                {
                    break;
                }

                // Which of the two it was is the whole difference between "it told us
                // it was going" and "it just stopped answering", so it travels.
                var departure = change is UPnP.Rx.Presence.DeviceLeft
                    ? DepartureReasons.Left
                    : DepartureReasons.Expired;

                var key = DtoMapper.NormalizeKey($"uuid:{uuid}");
                roster.Described.TryRemove(key, out _);
                roster.Discovered.TryRemove(key, out _);

                if (roster.Devices.TryRemove(key, out _))
                {
                    await hub.Clients.All.SendAsync(HubEvents.DeviceGone, key, departure, ct);
                }

                break;
            }
        }
    }

    private SsdpActivityDto ToActivityDto(Announcement announcement) => new(
        announcement.Kind.ToString(),
        ResolveCardKey(announcement.Device),
        // USNString, not ToUsnString(): the latter recomposes from the parsed parts and
        // throws when the entity part was unparsable, which since SSDP.UPnP.PCL 10.0.0
        // arrives here rather than being dropped. The activity log wants what the device
        // actually sent anyway.
        announcement.Device.Usn?.USNString,
        announcement.Device.Server?.FullString,
        announcement.Device.Location?.ToString(),
        announcement.Device.BootSignature.BootId,
        announcement.Device.BootSignature.Nls,
        announcement.NextBootId,
        announcement.Device.ConfigId,
        (int?)announcement.MaxAge?.TotalSeconds,
        announcement.Device.HasParsingError,
        announcement.Seen);

    /// <summary>
    /// Announcements for embedded devices carry the embedded UDN (a Sonos
    /// renderer's differs from its root's) - attribute them to the owning
    /// card, or every embedded announcement lands on a key no card renders.
    /// </summary>
    private string ResolveCardKey(DiscoveredDevice device)
    {
        var fallback = device.Usn?.DeviceUUID is { Length: > 0 } uuid
            ? DtoMapper.NormalizeKey($"uuid:{uuid}")
            : DtoMapper.NormalizeKey(device.Location?.ToString() ?? "?");

        foreach (var (key, described) in roster.Described)
        {
            if (described.Description.SelfAndDescendants().Any(d =>
                    d.Udn is not null
                    && string.Equals(DtoMapper.NormalizeKey(d.Udn), fallback, StringComparison.Ordinal)))
            {
                return key;
            }
        }

        return fallback;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _rescanLoop?.Dispose();
        _activity?.Dispose();
        // The rescan subject is not disposed abruptly: a straggling hub call
        // may still OnNext, which is a harmless no-op on an undisposed subject.
        // The client belongs to NetworkClientProvider; DI disposes it.
        return Task.CompletedTask;
    }
}
