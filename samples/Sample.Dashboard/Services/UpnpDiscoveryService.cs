using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reactive.Linq;
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
    private IDisposable? _deviceUp;
    private IDisposable? _deviceGone;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (network.Client is null)
        {
            logger.LogWarning("No usable IPv4 interfaces; the dashboard will stay empty.");
            return Task.CompletedTask;
        }

        logger.LogInformation(
            "Discovering from {Addresses}.", string.Join(", ", network.Addresses.Select(a => a.ToString())));

        var _client = network.Client;

        // DiscoverDevices (not DiscoverDescribedDevices): its per-subscription
        // dedup is USN+BOOTID, so periodic re-announcements can re-add a device
        // after a byebye removed it. Async work stays in the pipeline (Rx rule 1).
        _deviceUp = _client
            .DiscoverDevices()
            .SelectMany(device => Observable
                .FromAsync(async ct =>
                {
                    var described = await device.GetDescriptionAsync(ct);
                    return (Dto: ToDto(described), Described: described);
                })
                .Catch((UpnpException e) =>
                {
                    logger.LogDebug(e, "Skipping {Location}.", device.Location);
                    return Observable.Empty<(DeviceDto Dto, DescribedDevice Described)>();
                }))
            .SelectMany(pair => Observable.FromAsync(async ct =>
            {
                roster.Devices[pair.Dto.Key] = pair.Dto;
                roster.Described[pair.Dto.Key] = pair.Described;
                await hub.Clients.All.SendAsync(HubEvents.DeviceUp, pair.Dto, ct);
            }))
            .Subscribe(
                _ => { },
                e => logger.LogError(e, "The discovery stream terminated."));

        _deviceGone = _client
            .DeviceLost()
            .Select(device => device.Usn?.DeviceUUID)
            .Where(uuid => !string.IsNullOrEmpty(uuid))
            .Select(uuid => NormalizeKey($"uuid:{uuid}"))
            .SelectMany(key => Observable.FromAsync(async ct =>
            {
                roster.Described.TryRemove(key, out _);

                if (roster.Devices.TryRemove(key, out _))
                {
                    await hub.Clients.All.SendAsync(HubEvents.DeviceGone, key, ct);
                }
            }))
            .Subscribe(
                _ => { },
                e => logger.LogError(e, "The device-lost stream terminated."));

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _deviceUp?.Dispose();
        _deviceGone?.Dispose();
        // The client belongs to NetworkClientProvider; DI disposes it.
        return Task.CompletedTask;
    }

    private static DeviceDto ToDto(DescribedDevice described)
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

    private static DeviceNodeDto ToNode(UPnP.Rx.Model.DeviceDescription device) => new(
        FriendlyName: device.FriendlyName,
        DeviceType: device.DeviceType,
        Manufacturer: device.Manufacturer,
        Model: device.ModelName,
        Udn: device.Udn,
        Services: [.. device.Services.Select(s => s.ServiceType).OfType<string>()],
        Children: [.. device.EmbeddedDevices.Select(ToNode)]);

    private static string NormalizeKey(string raw) =>
        raw.Trim().ToLowerInvariant() is var lower && lower.StartsWith("uuid:", StringComparison.Ordinal)
            ? lower[5..]
            : lower;
}
