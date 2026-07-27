namespace UPnP.Rx.Presence;

/// <summary>What kind of SSDP envelope an <see cref="Announcement"/> reports.</summary>
public enum AnnouncementKind
{
    /// <summary>A unicast answer to an M-SEARCH someone sent.</summary>
    SearchResponse,

    /// <summary>A multicast <c>ssdp:alive</c> advertisement.</summary>
    Alive,

    /// <summary>A multicast <c>ssdp:byebye</c> goodbye.</summary>
    ByeBye,

    /// <summary>
    /// A multicast <c>ssdp:update</c> (UDA 2.0 clause 1.2.4): the device is changing
    /// its <c>BOOTID</c> because its network configuration changed, and is saying so
    /// in advance. It has <b>not</b> restarted and its state is intact.
    /// </summary>
    Update
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
/// when no usable lifetime was announced - and always null for byebyes, which revoke
/// an advertisement rather than carrying one. Null covers three upstream cases that
/// cannot currently be told apart (absent header, unparsable value, and a literal
/// <c>max-age=0</c>), because the SSDP layer reports all three as zero; none of them
/// is a lifetime a device can have meant, so the roster substitutes
/// <see cref="UpnpClientOptions.RosterExpiryFallback"/> for all of them.
/// </param>
/// <param name="Seen">When it arrived, stamped on the options' <see cref="TimeProvider"/>.</param>
public sealed record Announcement(
    AnnouncementKind Kind,
    DiscoveredDevice Device,
    TimeSpan? MaxAge,
    DateTimeOffset Seen);
