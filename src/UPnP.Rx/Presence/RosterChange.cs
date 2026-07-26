namespace UPnP.Rx.Presence;

/// <summary>
/// One change to the network's device roster (<see cref="UpnpClient.Roster"/>).
/// A small closed union, mirroring the eventing design: presence is data, and
/// per-item trouble never terminates the stream (Rx rule 6).
/// </summary>
/// <param name="Device">The device the change concerns - the last known discovery envelope.</param>
public abstract record RosterChange(DiscoveredDevice Device);

/// <summary>
/// A device joined the roster - or, when <paramref name="IsReplay"/> is true,
/// was already present when this subscriber attached (late subscribers first
/// receive the current roster as replay, then live changes, with no gap).
/// </summary>
/// <param name="Device">The device's discovery envelope.</param>
/// <param name="IsReplay">True when this reports existing state to a late subscriber rather than a live arrival.</param>
public sealed record DeviceAppeared(DiscoveredDevice Device, bool IsReplay) : RosterChange(Device);

/// <summary>
/// A known device changed: it rebooted (new <c>BOOTID</c> - descriptions are
/// re-read on next access), or a lazy re-describe after the description cache
/// lapsed found materially different content (self-healing; see the 4.1 plan).
/// </summary>
/// <param name="Device">The device's latest discovery envelope.</param>
public sealed record DeviceUpdated(DiscoveredDevice Device) : RosterChange(Device);

/// <summary>
/// A device's advertisement lapsed without a byebye - it vanished silently
/// (powered off, left the network). The deadline comes from the announcement's
/// <c>CACHE-CONTROL: max-age</c>, or <see cref="UpnpClientOptions.RosterExpiryFallback"/>
/// when absent, on the options' <see cref="TimeProvider"/>.
/// </summary>
/// <param name="Device">The device's last known discovery envelope.</param>
public sealed record DeviceExpired(DiscoveredDevice Device) : RosterChange(Device);

/// <summary>A device said <c>ssdp:byebye</c> - a deliberate goodbye.</summary>
/// <param name="Device">The device's last known discovery envelope.</param>
public sealed record DeviceLeft(DiscoveredDevice Device) : RosterChange(Device);
