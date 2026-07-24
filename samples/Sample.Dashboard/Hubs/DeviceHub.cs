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
    Services.NetworkClientProvider network) : Hub
{
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
    public async Task<ServiceDetailDto> GetServiceDetail(string deviceKey, string? udn, string serviceType)
    {
        if (!roster.Described.TryGetValue(deviceKey, out var described))
        {
            return new ServiceDetailDto(serviceType, [], [], "The device is no longer on the roster.");
        }

        try
        {
            var owner = described.Description
                .SelfAndDescendants()
                .FirstOrDefault(d => string.Equals(d.Udn, udn, StringComparison.OrdinalIgnoreCase))
                ?? described.Description;

            var serviceDescription = owner.Services.FirstOrDefault(s =>
                string.Equals(s.ServiceType, serviceType, StringComparison.OrdinalIgnoreCase));

            var service = serviceDescription is null
                ? null
                : described.Services.FirstOrDefault(s => Equals(s.Description, serviceDescription));

            if (service is null)
            {
                return new ServiceDetailDto(serviceType, [], [], "Service not found on the device.");
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
