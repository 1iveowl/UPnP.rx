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
}

/// <summary>
/// The only place that touches the network: owns the UpnpClient, projects
/// discovery into DTOs and broadcasts roster changes over SignalR. The
/// WebAssembly client never needs a socket.
/// </summary>
public sealed class UpnpDiscoveryService(
    IHubContext<DeviceHub> hub,
    DeviceRoster roster,
    ILogger<UpnpDiscoveryService> logger) : IHostedService
{
    private UpnpClient? _client;
    private IDisposable? _deviceUp;
    private IDisposable? _deviceGone;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus is OperationalStatus.Up
                && nic.NetworkInterfaceType is not NetworkInterfaceType.Loopback)
            .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
            .Select(u => u.Address)
            .Where(a => a.AddressFamily is AddressFamily.InterNetwork)
            .Distinct()
            .ToArray();

        if (addresses.Length is 0)
        {
            logger.LogWarning("No usable IPv4 interfaces; the dashboard will stay empty.");
            return Task.CompletedTask;
        }

        logger.LogInformation("Discovering from {Addresses}.", string.Join(", ", addresses.Select(a => a.ToString())));

        _client = new UpnpClient(new UpnpClientOptions(), addresses);

        // DiscoverDevices (not DiscoverDescribedDevices): its per-subscription
        // dedup is USN+BOOTID, so periodic re-announcements can re-add a device
        // after a byebye removed it. Async work stays in the pipeline (Rx rule 1).
        _deviceUp = _client
            .DiscoverDevices()
            .SelectMany(device => Observable
                .FromAsync(async ct =>
                {
                    var described = await device.GetDescriptionAsync(ct);
                    return ToDto(described);
                })
                .Catch((UpnpException e) =>
                {
                    logger.LogDebug(e, "Skipping {Location}.", device.Location);
                    return Observable.Empty<DeviceDto>();
                }))
            .SelectMany(dto => Observable.FromAsync(async ct =>
            {
                roster.Devices[dto.Key] = dto;
                await hub.Clients.All.SendAsync("DeviceUp", dto, ct);
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
                if (roster.Devices.TryRemove(key, out _))
                {
                    await hub.Clients.All.SendAsync("DeviceGone", key, ct);
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
        _client?.Dispose();
        return Task.CompletedTask;
    }

    private static DeviceDto ToDto(DescribedDevice described)
    {
        var root = described.Description;
        var all = root.SelfAndDescendants().ToList();

        return new DeviceDto(
            Key: NormalizeKey(root.Udn ?? root.Location.ToString()),
            FriendlyName: root.FriendlyName,
            DeviceType: root.DeviceType,
            Manufacturer: root.Manufacturer,
            Model: root.ModelName,
            Location: root.Location.ToString(),
            Services: [.. all
                .SelectMany(d => d.Services)
                .Select(s => s.ServiceType)
                .Where(t => t is not null)
                .Select(t => t!)
                .Distinct()],
            DeviceCount: all.Count);
    }

    private static string NormalizeKey(string raw) =>
        raw.Trim().ToLowerInvariant() is var lower && lower.StartsWith("uuid:", StringComparison.Ordinal)
            ? lower[5..]
            : lower;
}
