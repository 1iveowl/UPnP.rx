using Microsoft.AspNetCore.SignalR;
using Sample.Dashboard.Client.Models;
using Sample.Dashboard.Services;
using UPnP.Rx;

namespace Sample.Dashboard.Hubs;

/// <summary>
/// Pushes DeviceUp/DeviceGone to every browser; replays the current roster to
/// each newly connected client so late joiners see the full picture; and serves
/// on-demand SCPD detail when the user unfolds a service.
/// </summary>
public sealed class DeviceHub(
    DeviceRoster roster,
    GatewayService gatewayService,
    Services.NetworkClientProvider network,
    Services.UpnpDiscoveryService discovery) : Hub
{
    /// <summary>
    /// Clears the roster and searches the network afresh. Restarting the
    /// discovery subscription resets its per-subscription dedup, so devices
    /// swallowed by a failed describe or a same-BOOTID re-announcement come
    /// back; the RosterReset broadcast puts every client into rescan mode
    /// (live watches end at once, stale cards gray out until fresh ones land).
    /// </summary>
    public Task Rescan() => discovery.RescanAsync();

    /// <summary>
    /// Drops the cached description and re-reads the device - the manual heal
    /// for a stale/sparse read (full self-healing is a v3.1 investigation).
    /// </summary>
    public async Task<string?> RefreshDevice(string key)
    {
        if (!roster.Discovered.TryGetValue(key, out var discovered) || discovered.Location is null)
        {
            return "Unknown device.";
        }

        network.Client?.InvalidateDescriptions(discovered.Location);

        try
        {
            var described = await discovered.GetDescriptionAsync(Context.ConnectionAborted);
            var dto = Services.DtoMapper.ToDto(described);

            roster.Devices[dto.Key] = dto;
            roster.Described[dto.Key] = described;
            await Clients.All.SendAsync(HubEvents.DeviceUp, dto);
            return null;
        }
        catch (UpnpException e)
        {
            return e.Message;
        }
    }

    /// <summary>The gateway's identity + WAN state, or null when none answered the search.</summary>
    public Task<Client.Models.GatewayDto?> GetGatewayInfo() => gatewayService.GetGatewayInfoAsync();

    /// <summary>The gateway's current port-mapping table.</summary>
    public Task<Client.Models.PortMappingDto[]> GetPortMappings() => gatewayService.GetPortMappingsAsync();

    /// <summary>Creates an auto-renewing mapping held by the server; returns an error message or null.</summary>
    public Task<string?> AddPortMapping(ushort externalPort, ushort internalPort, string protocol, string description, int leaseMinutes) =>
        gatewayService.AddPortMappingAsync(externalPort, internalPort, protocol, description, leaseMinutes);

    /// <summary>Deletes a mapping; returns an error message or null.</summary>
    public Task<string?> DeletePortMapping(ushort externalPort, string protocol) =>
        gatewayService.DeletePortMappingAsync(externalPort, protocol);

    public override async Task OnConnectedAsync()
    {
        foreach (var device in roster.Devices.Values)
        {
            await Clients.Caller.SendAsync(HubEvents.DeviceUp, device);
        }

        await base.OnConnectedAsync();
    }

    /// <summary>
    /// Fetches (and, in the library, caches) the SCPD for one service of one
    /// device in the tree, identified by the owning node's UDN + service type.
    /// </summary>
    private UpnpService? FindService(string deviceKey, string? udn, string serviceType)
    {
        if (!roster.Described.TryGetValue(deviceKey, out var described))
        {
            return null;
        }

        var owner = described.Description
            .SelfAndDescendants()
            .FirstOrDefault(d => string.Equals(d.Udn, udn, StringComparison.OrdinalIgnoreCase))
            ?? described.Description;

        var serviceDescription = owner.Services.FirstOrDefault(s =>
            string.Equals(s.ServiceType, serviceType, StringComparison.OrdinalIgnoreCase));

        return serviceDescription is null
            ? null
            : described.Services.FirstOrDefault(s => Equals(s.Description, serviceDescription));
    }

    /// <summary>
    /// Streams a service's live GENA events to the browser; the Rx subscription
    /// (and with it the device-side GENA subscription, when this is the last
    /// watcher) ends when the client cancels the stream.
    /// </summary>
    public async IAsyncEnumerable<ServiceEventDto> StreamServiceEvents(
        string deviceKey, string? udn, string serviceType,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var service = FindService(deviceKey, udn, serviceType)
            ?? throw new HubException("Service not found on the roster.");

        var channel = System.Threading.Channels.Channel.CreateBounded<ServiceEventDto>(
            new System.Threading.Channels.BoundedChannelOptions(64)
            {
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest
            });

        using var subscription = service.Events().Subscribe(
            e => channel.Writer.TryWrite(ToDto(e)),
            error =>
            {
                // A terminal stream error (e.g. a permanent SUBSCRIBE refusal)
                // is information for the viewer, not a hub failure - completing
                // the channel with the exception would fault the SignalR
                // invocation and litter the server log. Deliver the message as
                // the stream's last item and end normally instead.
                channel.Writer.TryWrite(new ServiceEventDto("StreamError", null, null, 0, false, false, error.Message));
                channel.Writer.TryComplete();
            },
            () => channel.Writer.TryComplete());

        // Cancellation here is the client ending the watch (stop button,
        // rescan, tab closed) - a normal end of the stream, not an error.
        // ReadAllAsync(ct) would exit by throwing OperationCanceledException
        // through this frame (harmless to SignalR, but it trips the debugger's
        // user-unhandled break on every stop); read explicitly and turn
        // cancellation into a quiet break instead.
        while (true)
        {
            ServiceEventDto? dto;

            try
            {
                if (!await channel.Reader.WaitToReadAsync(ct))
                {
                    break;                       // stream completed
                }

                if (!channel.Reader.TryRead(out dto))
                {
                    continue;
                }
            }
            catch (OperationCanceledException)
            {
                break;                           // client walked away
            }

            yield return dto;
        }
    }

    private static ServiceEventDto ToDto(UPnP.Rx.Eventing.UpnpEvent e) => e switch
    {
        UPnP.Rx.Eventing.PropertyChange c =>
            new ServiceEventDto("PropertyChange", c.Name, c.Value, c.Seq, c.IsInitialState, c.IsReplay, null),
        UPnP.Rx.Eventing.Subscribed s =>
            new ServiceEventDto("Subscribed", null, s.Sid, 0, false, false, $"timeout {s.Timeout}"),
        UPnP.Rx.Eventing.Resubscribed r =>
            new ServiceEventDto("Resubscribed", null, r.Sid, 0, false, false, null),
        UPnP.Rx.Eventing.RenewalFailed f =>
            new ServiceEventDto("RenewalFailed", null, null, 0, false, false, f.Message),
        UPnP.Rx.Eventing.GapDetected g =>
            new ServiceEventDto("GapDetected", null, null, g.ActualSeq, false, false,
                $"expected {g.ExpectedSeq}"),
        UPnP.Rx.Eventing.SubscriptionRefused refused =>
            new ServiceEventDto("SubscriptionRefused", null, null, 0, false, false, refused.Reason),
        _ => new ServiceEventDto(e.GetType().Name, null, null, 0, false, false, null)
    };

    public async Task<ServiceDetailDto> GetServiceDetail(string deviceKey, string? udn, string serviceType)
    {
        try
        {
            var service = FindService(deviceKey, udn, serviceType);

            if (service is null)
            {
                return new ServiceDetailDto(serviceType, [], [], "Service not found on the roster.");
            }

            var scpd = await service.GetScpdAsync(Context.ConnectionAborted);

            return new ServiceDetailDto(
                serviceType,
                [.. scpd.Actions.Select(a => new ActionDto(
                    a.Name ?? "(unnamed)",
                    [.. a.Arguments
                        .Where(arg => arg.Direction != UPnP.Rx.Model.ArgumentDirection.Out)
                        .Select(arg => arg.Name ?? "?")],
                    [.. a.Arguments
                        .Where(arg => arg.Direction == UPnP.Rx.Model.ArgumentDirection.Out)
                        .Select(arg => arg.Name ?? "?")]))],
                [.. scpd.StateVariables.Select(v => new StateVariableDto(
                    v.Name ?? "(unnamed)",
                    v.DataType,
                    [.. v.AllowedValues]))],
                Error: null);
        }
        catch (UpnpException e)
        {
            return new ServiceDetailDto(serviceType, [], [], e.Message);
        }
    }
}
