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
public sealed class DeviceHub(DeviceRoster roster) : Hub
{
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
