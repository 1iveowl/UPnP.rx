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

namespace Sample.Dashboard.Services;

/// <summary>The server-side device roster, replayed to newly connected clients.</summary>
public sealed class DeviceRoster
{
    public ConcurrentDictionary<string, DeviceDto> Devices { get; } = new();

    /// <summary>The live library objects behind the DTOs - the hub uses them for on-demand SCPD fetches.</summary>
    public ConcurrentDictionary<string, DescribedDevice> Described { get; } = new();

    /// <summary>The discovery envelopes - the hub uses them to re-describe a device on demand.</summary>
    public ConcurrentDictionary<string, DiscoveredDevice> Discovered { get; } = new();
}

/// <summary>Maps library objects to the wire DTOs; shared by the discovery service and the hub.</summary>
internal static class DtoMapper
{
    internal static DeviceDto ToDto(DescribedDevice described)
    {
        var root = described.Description;
        var all = root.SelfAndDescendants().ToList();

        return new DeviceDto(
            Key: NormalizeKey(root.Udn ?? root.Location.ToString()),
            FriendlyName: root.FriendlyName,
            Manufacturer: root.Manufacturer,
            Model: root.ModelName,
            Location: root.Location.ToString(),
            ServiceCount: all.SelectMany(d => d.Services).Count(),
            DeviceCount: all.Count,
            Root: ToNode(root));
    }

    internal static DeviceNodeDto ToNode(UPnP.Rx.Model.DeviceDescription device) => new(
        FriendlyName: device.FriendlyName,
        DeviceType: device.DeviceType,
        Manufacturer: device.Manufacturer,
        Model: device.ModelName,
        Udn: device.Udn,
        Services: [.. device.Services.Select(s => s.ServiceType).OfType<string>()],
        Children: [.. device.EmbeddedDevices.Select(ToNode)]);

    internal static string NormalizeKey(string raw) =>
        raw.Trim().ToLowerInvariant() is var lower && lower.StartsWith("uuid:", StringComparison.Ordinal)
            ? lower[5..]
            : lower;
}

/// <summary>
/// Projects discovery into DTOs and broadcasts roster changes over SignalR.
/// The WebAssembly client never needs a socket; the shared UpnpClient comes
/// from <see cref="NetworkClientProvider"/> (the gateway service uses it too).
/// </summary>
public sealed class UpnpDiscoveryService(
    NetworkClientProvider network,
    IHubContext<DeviceHub> hub,
    DeviceRoster roster,
    ILogger<UpnpDiscoveryService> logger) : IHostedService
{
    private readonly Subject<Unit> _rescans = new();
    private IDisposable? _rescanLoop;
    private IDisposable? _generation;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (network.Client is null)
        {
            logger.LogWarning("No usable IPv4 interfaces; the dashboard will stay empty.");
            return Task.CompletedTask;
        }

        logger.LogInformation(
            "Discovering from {Addresses}.", string.Join(", ", network.Addresses.Select(a => a.ToString())));

        // A rescan is a stream event; Synchronize serializes requests arriving
        // from concurrent hub connections (the Rx grammar requires serialized
        // OnNext), which also makes the generation swap below single-threaded.
        //
        // The swap deliberately OVERLAPS generations - new subscription first,
        // old disposal second (which is why this isn't Switch: Switch disposes
        // first). The control point's shared socket streams are RefCounted;
        // letting them hit zero subscribers mid-rescan tears the sockets down
        // while the replacement generation is restarting them, and the accept
        // loops upstream lose that race (SocketException 89 kills the fresh
        // generation). Overlap keeps the sockets alive throughout; the new
        // subscription still sends a fresh M-SEARCH and starts its dedup state
        // from scratch.
        _rescanLoop = _rescans
            .Synchronize()
            .StartWith(Unit.Default)
            .Subscribe(_ =>
            {
                var previous = _generation;

                _generation = DiscoveryPipeline(network.Client).Subscribe(
                    _ => { },
                    e => logger.LogError(e, "The discovery pipeline terminated."));

                previous?.Dispose();
            });

        return Task.CompletedTask;
    }

    /// <summary>
    /// The manual heal for a roster gone stale: a device is invisible until the
    /// server restarts when its describe failed once, or when it re-announced
    /// with an unchanged BOOTID after a byebye - either way the long-lived
    /// subscription's dedup swallows every later announcement. A rescan swaps
    /// in a fresh pipeline (fresh M-SEARCH, fresh dedup state); the RosterReset
    /// broadcast tells every client to drop its cards, which ends their live
    /// watches through component disposal (UNSUBSCRIBE on device).
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

    /// <summary>
    /// One discovery generation: device-up and device-lost handling merged into
    /// a single subscribable pipeline. Either half dying is logged and ends
    /// quietly (the other keeps running); the next rescan rebuilds both.
    /// </summary>
    private IObservable<Unit> DiscoveryPipeline(UpnpClient client) =>
        Observable.Merge(
            DeviceUpStream(client).Catch((Exception e) =>
            {
                logger.LogError(e, "The discovery stream terminated.");
                return Observable.Empty<Unit>();
            }),
            DeviceGoneStream(client).Catch((Exception e) =>
            {
                logger.LogError(e, "The device-lost stream terminated.");
                return Observable.Empty<Unit>();
            }));

    // DiscoverDevices (not DiscoverDescribedDevices): its per-subscription
    // dedup is USN+BOOTID, so periodic re-announcements can re-add a device
    // after a byebye removed it. Async work stays in the pipeline (Rx rule 1).
    private IObservable<Unit> DeviceUpStream(UpnpClient client) =>
        client
            .DiscoverDevices()
            .SelectMany(device => Observable
                .FromAsync(async ct =>
                {
                    var described = await device.GetDescriptionAsync(ct);
                    return (Dto: DtoMapper.ToDto(described), Described: described, Envelope: device);
                })
                .Catch((UpnpException e) =>
                {
                    logger.LogDebug(e, "Skipping {Location}.", device.Location);
                    return Observable.Empty<(DeviceDto Dto, DescribedDevice Described, DiscoveredDevice Envelope)>();
                }))
            .SelectMany(pair => Observable.FromAsync(async ct =>
            {
                roster.Devices[pair.Dto.Key] = pair.Dto;
                roster.Described[pair.Dto.Key] = pair.Described;
                roster.Discovered[pair.Dto.Key] = pair.Envelope;
                await hub.Clients.All.SendAsync(HubEvents.DeviceUp, pair.Dto, ct);
            }));

    private IObservable<Unit> DeviceGoneStream(UpnpClient client) =>
        client
            .DeviceLost()
            .Select(device => device.Usn?.DeviceUUID)
            .Where(uuid => !string.IsNullOrEmpty(uuid))
            .Select(uuid => DtoMapper.NormalizeKey($"uuid:{uuid}"))
            .SelectMany(key => Observable.FromAsync(async ct =>
            {
                roster.Described.TryRemove(key, out _);
                roster.Discovered.TryRemove(key, out _);

                if (roster.Devices.TryRemove(key, out _))
                {
                    await hub.Clients.All.SendAsync(HubEvents.DeviceGone, key, ct);
                }
            }));

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _rescanLoop?.Dispose();
        _generation?.Dispose();
        // The rescan subject is not disposed abruptly: a straggling hub call
        // may still OnNext, which is a harmless no-op on an undisposed subject.
        // The client belongs to NetworkClientProvider; DI disposes it.
        return Task.CompletedTask;
    }
}
