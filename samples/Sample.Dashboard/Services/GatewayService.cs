using System.Reactive.Linq;
using Microsoft.AspNetCore.SignalR;
using Sample.Dashboard.Client.Models;
using Sample.Dashboard.Hubs;
using UPnP.Rx;
using UPnP.Rx.PortMapping;

namespace Sample.Dashboard.Services;

/// <summary>
/// The port-mapping side of the dashboard: resolves the internet gateway over
/// the shared <see cref="UpnpClient"/>, creates and holds auto-renewing
/// <see cref="PortMappingLease"/>s, and forwards their renewal events to every
/// browser. All router mutation happens here, on the server.
/// </summary>
public sealed class GatewayService(
    NetworkClientProvider network,
    IHubContext<DeviceHub> hub,
    ILogger<GatewayService> logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly Dictionary<(ushort Port, string Protocol), (PortMappingLease Lease, IDisposable Events)> _held = [];
    private InternetGateway? _gateway;
    private bool _searched;

    /// <summary>The gateway, discovered once on first use; null when none answered.</summary>
    public async Task<InternetGateway?> GetGatewayAsync()
    {
        if (_gateway is not null)
        {
            return _gateway;
        }

        if (network.Client is null)
        {
            return null;
        }

        await _mutex.WaitAsync();

        try
        {
            // One search per process unless it failed - a later call retries.
            if (_gateway is null && !_searched)
            {
                _searched = true;
                _gateway = await PortMapper.DiscoverGatewayAsync(network.Client, TimeSpan.FromSeconds(10));
                _searched = _gateway is not null;
            }

            return _gateway;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<GatewayDto?> GetGatewayInfoAsync()
    {
        var gateway = await GetGatewayAsync();

        if (gateway is null)
        {
            return null;
        }

        string? externalIp = null;
        ConnectionStatusInfo? status = null;

        try
        {
            status = await gateway.GetStatusInfoAsync();
            externalIp = (await gateway.GetExternalIPAddressAsync()).ToString();
        }
        catch (UpnpException e)
        {
            logger.LogDebug(e, "Gateway state query failed.");
        }

        return new GatewayDto(
            FriendlyName: gateway.Device.Description.FriendlyName,
            WanServiceType: gateway.WanConnectionService.Description.ServiceType,
            ExternalIp: externalIp,
            Status: status?.Status,
            IsConnected: status?.IsConnected ?? false,
            LastError: status?.LastError,
            UptimeSeconds: status?.Uptime.TotalSeconds ?? 0,
            LocalAddress: gateway.LocalAddress?.ToString());
    }

    public async Task<PortMappingDto[]> GetPortMappingsAsync()
    {
        var gateway = await GetGatewayAsync();

        if (gateway is null)
        {
            return [];
        }

        var mappings = new List<PortMappingDto>();

        await foreach (var m in gateway.GetPortMappingsAsync())
        {
            mappings.Add(new PortMappingDto(
                m.Protocol.ToWireString(),
                m.ExternalPort,
                m.InternalPort,
                m.InternalClient,
                m.Enabled,
                m.Description,
                m.LeaseDuration.TotalSeconds,
                HeldByServer: _held.ContainsKey((m.ExternalPort, m.Protocol.ToWireString()))));
        }

        return [.. mappings];
    }

    /// <summary>Creates an auto-renewing mapping and holds its lease; renewal events go to all browsers.</summary>
    public async Task<string?> AddPortMappingAsync(
        ushort externalPort, ushort internalPort, string protocol, string description, int leaseMinutes)
    {
        var gateway = await GetGatewayAsync();

        if (gateway is null)
        {
            return "No gateway available.";
        }

        var proto = ToProtocol(protocol);

        try
        {
            var lease = await gateway.AddPortMappingAsync(
                externalPort, internalPort, proto,
                description.Length is 0 ? "UPnP.Rx dashboard" : description,
                TimeSpan.FromMinutes(leaseMinutes));

            // The lease event stream made visible: forward every renewal outcome
            // to the browsers (async stays in the pipeline, per the house rules).
            var events = lease.Events
                .SelectMany(e => Observable.FromAsync(ct => hub.Clients.All.SendAsync(
                    HubEvents.LeaseEvent,
                    new LeaseEventDto(
                        lease.Mapping.ExternalPort,
                        lease.Mapping.Protocol.ToWireString(),
                        e.Kind.ToString(),
                        e.Message,
                        TimeProvider.System.GetUtcNow()),
                    ct)))
                .Subscribe(
                    _ => { },
                    e => logger.LogDebug(e, "Lease event forwarding ended."));

            _held[HeldKey(lease.Mapping.ExternalPort, lease.Mapping.Protocol)] = (lease, events);
            return null;
        }
        catch (UpnpException e)
        {
            return e.Message;
        }
    }

    private static Protocol ToProtocol(string protocol) =>
        string.Equals(protocol, "UDP", StringComparison.OrdinalIgnoreCase) ? Protocol.Udp : Protocol.Tcp;

    // One key helper for both sides of the dictionary: writing with ToWireString() and
    // reading with ToUpperInvariant() agreed only because both happen to yield "TCP".
    private static (ushort Port, string Protocol) HeldKey(ushort externalPort, Protocol protocol) =>
        (externalPort, protocol.ToWireString());

    /// <summary>Deletes a mapping - via the held lease when it is ours (graceful disposal), plain delete otherwise.</summary>
    public async Task<string?> DeletePortMappingAsync(ushort externalPort, string protocol)
    {
        var gateway = await GetGatewayAsync();

        if (gateway is null)
        {
            return "No gateway available.";
        }

        try
        {
            if (_held.Remove(HeldKey(externalPort, ToProtocol(protocol)), out var held))
            {
                held.Events.Dispose();
                await held.Lease.DisposeAsync();     // graceful: stops renewal + deletes on the router
                return null;
            }

            await gateway.DeletePortMappingAsync(externalPort, ToProtocol(protocol));
            return null;
        }
        catch (UpnpException e)
        {
            return e.Message;
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (lease, events) in _held.Values)
        {
            events.Dispose();
            await lease.DisposeAsync();              // good citizenship: clean the router on shutdown
        }

        _held.Clear();
        _mutex.Dispose();
    }
}
