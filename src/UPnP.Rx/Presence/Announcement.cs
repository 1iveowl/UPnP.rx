namespace UPnP.Rx.Presence;

/// <summary>What kind of SSDP envelope an <see cref="Announcement"/> reports.</summary>
public enum AnnouncementKind
{
    /// <summary>A unicast answer to an M-SEARCH someone sent.</summary>
    SearchResponse,

    /// <summary>A multicast <c>ssdp:alive</c> advertisement.</summary>
    Alive,

    /// <summary>A multicast <c>ssdp:byebye</c> goodbye.</summary>
    ByeBye
}

/// <summary>
/// One parsed SSDP envelope as it arrived - the device activity timeline's
/// currency (<see cref="UpnpClient.Announcements"/>). Undeduplicated: every
/// periodic re-advertisement is one of these.
/// </summary>
/// <param name="Kind">Search response, alive, or byebye.</param>
/// <param name="Device">The parsed discovery envelope (byebyes carry no location).</param>
/// <param name="MaxAge">
/// The advertised lifetime (<c>CACHE-CONTROL: max-age</c>), or <see langword="null"/>
/// when the device announced none - and always null for byebyes, which revoke an
/// advertisement rather than carrying one.
/// </param>
/// <param name="Seen">When it arrived, stamped on the options' <see cref="TimeProvider"/>.</param>
public sealed record Announcement(
    AnnouncementKind Kind,
    DiscoveredDevice Device,
    TimeSpan? MaxAge,
    DateTimeOffset Seen);
