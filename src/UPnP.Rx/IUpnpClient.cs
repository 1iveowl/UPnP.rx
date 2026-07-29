using SSDP.UPnP.PCL.Model;
using UPnP.Rx.Presence;

namespace UPnP.Rx;

/// <summary>
/// The UPnP control point client's surface, for consumers who mock or decorate
/// it in their own tests (added 4.2; <see cref="UpnpClient"/> is the
/// implementation and carries the full documentation).
/// </summary>
public interface IUpnpClient : IAsyncDisposable, IDisposable
{
    /// <summary>Devices announcing themselves, deduplicated per subscription; an M-SEARCH is sent on subscribe. See <see cref="UpnpClient.DiscoverDevices"/>.</summary>
    IObservable<DiscoveredDevice> DiscoverDevices(ST? searchTarget = null, MxSeconds? mx = null);

    /// <summary>Discovery composed with the (cached) description fetch, deduplicated by UDN. See <see cref="UpnpClient.DiscoverDescribedDevices"/>.</summary>
    IObservable<DescribedDevice> DiscoverDescribedDevices(ST? searchTarget = null, MxSeconds? mx = null);

    /// <summary>Devices leaving the network (<c>ssdp:byebye</c>). See <see cref="UpnpClient.DeviceLost"/>.</summary>
    IObservable<DiscoveredDevice> DeviceLost();

    /// <summary>The device roster: presence changes with replay, expiry and self-healing. See <see cref="UpnpClient.Roster"/>.</summary>
    IObservable<RosterChange> Roster();

    /// <summary>Every parsed SSDP envelope, undeduplicated - the activity timeline. See <see cref="UpnpClient.Announcements"/>.</summary>
    IObservable<Announcement> Announcements();

    /// <summary>One M-SEARCH burst on every interface, soliciting without subscribing or resetting anything. See <see cref="UpnpClient.SearchAsync"/>.</summary>
    Task SearchAsync(ST? searchTarget = null, MxSeconds? mx = null, CancellationToken ct = default);

    /// <summary>Drops every cached description for the location, forcing a re-fetch. See <see cref="UpnpClient.InvalidateDescriptions"/>.</summary>
    void InvalidateDescriptions(Uri location);
}
