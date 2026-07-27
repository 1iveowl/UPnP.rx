using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Sample.Dashboard.Client.Models;
using Sample.Dashboard.Services;
using UPnP.Rx;
using UPnP.Rx.Model;

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
    Services.UpnpDiscoveryService discovery,
    ILogger<DeviceHub> logger) : Hub
{
    /// <summary>
    /// Clears the projection and swaps in a fresh library-roster subscription
    /// (fresh engine, fresh M-SEARCH) - the manual big hammer next to the
    /// roster's automatic healing. The RosterReset broadcast puts every client
    /// into rescan mode (live watches end at once, stale cards gray out until
    /// fresh ones land).
    /// </summary>
    public Task Rescan() => discovery.RescanAsync();

    /// <summary>
    /// One M-SEARCH burst, nothing reset: devices answer within MX seconds and
    /// the responses flow into the roster and the activity logs. The light
    /// sibling of <see cref="Rescan"/>.
    /// </summary>
    public Task Probe() => network.Client?.SearchAsync(ct: Context.ConnectionAborted) ?? Task.CompletedTask;

    /// <summary>
    /// Drops the cached description and re-reads the device on demand - the
    /// per-device manual heal beside the roster's automatic one (which only
    /// re-describes after the cached TTL lapses).
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
            var dto = roster.Record(described, discovered);

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
            e =>
            {
                foreach (var dto in ToDtos(e))
                {
                    channel.Writer.TryWrite(dto);
                }
            },
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

    /// <summary>
    /// AV services event a single LastChange variable of escaped XML; decode it
    /// into one readable row per variable (Kind "AvChange", channel and
    /// instance surfaced) - the quick controls and the live feed both ride
    /// these. Unparsable payloads fall back to the raw row (leniency).
    /// </summary>
    private IEnumerable<ServiceEventDto> ToDtos(UPnP.Rx.Eventing.UpnpEvent e)
    {
        if (e is UPnP.Rx.Eventing.PropertyChange change
            && string.Equals(change.Name, "LastChange", StringComparison.OrdinalIgnoreCase))
        {
            var parsed = UPnP.Rx.Eventing.Av.LastChangeParser.Parse(change.Value);

            if (parsed.IsSuccess && parsed.Value.Count > 0)
            {
                foreach (var av in parsed.Value)
                {
                    yield return new ServiceEventDto(
                        "AvChange", av.Name, av.Value, change.Seq,
                        change.IsInitialState, change.IsReplay, null,
                        av.Channel, av.InstanceId);
                }

                yield break;
            }
        }

        yield return ToDto(e);
    }

    private ServiceEventDto ToDto(UPnP.Rx.Eventing.UpnpEvent e) => e switch
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
        UPnP.Rx.Eventing.SubscriptionCancelled cancelled =>
            new ServiceEventDto("SubscriptionCancelled", null, null, 0, false, false,
                cancelled.WillResubscribe
                    ? $"{cancelled.Reason} Resubscribing."
                    : cancelled.Reason),
        // Never silently drop an event the library grew after this mapping was
        // written: records synthesise a full property dump, so the row still carries
        // everything, and the log names what needs mapping.
        _ => Unmapped(e)
    };

    private ServiceEventDto Unmapped(UPnP.Rx.Eventing.UpnpEvent e)
    {
        logger.LogWarning(
            "No dashboard mapping for the event type {EventType}; showing it raw. Add it to DeviceHub.ToDto.",
            e.GetType().Name);

        return new ServiceEventDto(e.GetType().Name, null, null, 0, false, false, e.ToString());
    }

    /// <summary>The input metadata the SCPD holds for an in-argument, via its related state variable.</summary>
    private static ArgumentDto ToArgumentDto(UPnP.Rx.Model.ArgumentDescription argument, UPnP.Rx.Model.Scpd scpd)
    {
        var variable = scpd.StateVariables.FirstOrDefault(v =>
            string.Equals(v.Name, argument.RelatedStateVariable, StringComparison.OrdinalIgnoreCase));

        return new ArgumentDto(
            argument.Name ?? "?",
            variable?.DataType,
            [.. variable?.AllowedValues ?? []],
            variable?.AllowedRange?.Minimum,
            variable?.AllowedRange?.Maximum,
            variable?.DefaultValue);
    }

    /// <summary>
    /// Invokes a SOAP action with the given arguments: SCPD-validated and
    /// ordered by the library, faults returned in the device's own words.
    /// The confirm-step lives client-side (4.1 decision Q3).
    /// </summary>
    public async Task<InvokeResultDto> InvokeAction(
        string deviceKey, string? udn, string serviceType, string actionName, Dictionary<string, string> arguments)
    {
        var service = FindService(deviceKey, udn, serviceType);

        if (service is null)
        {
            return new InvokeResultDto([], "Service not found on the roster.");
        }

        try
        {
            var scpd = await service.GetScpdAsync(Context.ConnectionAborted);
            var ordered = scpd.ValidateAndOrderArguments(actionName, arguments);

            if (!ordered.IsSuccess)
            {
                return new InvokeResultDto([], ordered.Error);
            }

            var result = await service.InvokeAsync(actionName, ordered.Value, Context.ConnectionAborted);

            return new InvokeResultDto(
                [.. result.Out.Select(pair => new OutValueDto(pair.Key, pair.Value))],
                null,
                Services.DtoMapper.SoleVersion(result.VersionClaims));
        }
        catch (UpnpActionException e)
        {
            // When the device sends no errorDescription, at least say what the
            // code's range means (UDA 2.0 error-code table).
            var description = e.Error.Description ?? e.Error.Code switch
            {
                >= 600 and <= 699 => "no description; 600-699 is the UPnP common action error range",
                >= 700 and <= 799 => "no description; 700-799 is defined by the service's own spec",
                >= 800 and <= 899 => "no description; 800-899 is the vendor-specific range - the device refuses without saying why",
                _ => "no description"
            };

            return new InvokeResultDto([], $"The device refused: UPnP error {e.Error.Code} ({description}).");
        }
        catch (UpnpException e)
        {
            return new InvokeResultDto([], e.Message);
        }
    }

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
                        .Select(arg => ToArgumentDto(arg, scpd))],
                    [.. a.Arguments
                        .Where(arg => arg.Direction == UPnP.Rx.Model.ArgumentDirection.Out)
                        .Select(arg => arg.Name ?? "?")]))],
                [.. scpd.StateVariables.Select(v => new StateVariableDto(
                    v.Name ?? "(unnamed)",
                    v.DataType,
                    [.. v.AllowedValues]))],
                Error: null,
                // Available only now: the SCPD is what carries a service's own
                // specVersion (UDA 2.0 clause 2.5), and it can contradict its device.
                UpnpVersion: Services.DtoMapper.SoleVersion(service.VersionClaims));
        }
        catch (UpnpException e)
        {
            return new ServiceDetailDto(serviceType, [], [], e.Message);
        }
    }
}
