using Microsoft.AspNetCore.SignalR;
using Sample.Dashboard.Services;

namespace Sample.Dashboard.Hubs;

/// <summary>
/// Pushes DeviceUp/DeviceGone to every browser; replays the current roster to
/// each newly connected client so late joiners see the full picture.
/// </summary>
public sealed class DeviceHub(DeviceRoster roster) : Hub
{
    public override async Task OnConnectedAsync()
    {
        foreach (var device in roster.Devices.Values)
        {
            await Clients.Caller.SendAsync("DeviceUp", device);
        }

        await base.OnConnectedAsync();
    }
}
