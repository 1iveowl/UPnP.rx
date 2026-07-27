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
/// <param name="Kind">Search response, alive, byebye, or update.</param>
/// <param name="Device">The parsed discovery envelope (byebyes carry no location).</param>
/// <param name="MaxAge">
/// Exactly what the device advertised in <c>CACHE-CONTROL: max-age</c>:
/// <see langword="null"/> when it announced no usable lifetime (no header, no
/// <c>max-age</c> directive, or an unparsable one), and <see cref="TimeSpan.Zero"/>
/// when it genuinely said zero - those are different statements and are reported as
/// such. Always null for byebyes, which revoke an advertisement rather than carrying
/// one. What the roster does with a zero lifetime is its own decision; see
/// <see cref="UpnpClientOptions.RosterExpiryFallback"/>.
/// </param>
/// <param name="Seen">When it arrived, stamped on the options' <see cref="TimeProvider"/>.</param>
/// <param name="NextBootId">
/// The boot identity the device says it is moving to
/// (<c>NEXTBOOTID.UPNP.ORG</c>), set only on <see cref="AnnouncementKind.Update"/>.
/// It is the whole point of an <c>ssdp:update</c>: the message carries the device's
/// <i>current</i> BOOTID, and this is the one every later message will carry.
/// </param>
public sealed record Announcement(
    AnnouncementKind Kind,
    DiscoveredDevice Device,
    TimeSpan? MaxAge,
    DateTimeOffset Seen,
    uint? NextBootId = null);
