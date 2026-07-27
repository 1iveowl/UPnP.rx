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
/// A known device's description changed under it: a lazy re-describe after the
/// description cache lapsed found materially different content (self-healing; see
/// the 4.1 plan). The device stayed up throughout - a restart is
/// <see cref="DeviceRebooted"/>.
/// </summary>
/// <param name="Device">The device's latest discovery envelope.</param>
public sealed record DeviceUpdated(DiscoveredDevice Device) : RosterChange(Device);

/// <summary>
/// A known device restarted: its boot identity changed, so everything held about it
/// is void.
/// </summary>
/// <remarks>
/// UDA 2.0 clause 1.2.4 is explicit about what this means to a control point - on
/// seeing a different BOOTID "any stored state information about the device has
/// become invalid. It shall treat the device as a newly discovered device." That is
/// a stronger statement than <see cref="DeviceUpdated"/> makes: event subscriptions
/// are gone, cached action results are meaningless, and the description is re-read
/// on next access. It is reported separately so a consumer holding device-scoped
/// state can tell "throw it all away" from "the XML moved".
/// <para>
/// A device that changes network configuration rather than restarting announces that
/// with <c>ssdp:update</c> first, and that path deliberately does not raise this.
/// </para>
/// </remarks>
/// <param name="Device">The device's discovery envelope carrying the new boot identity.</param>
public sealed record DeviceRebooted(DiscoveredDevice Device) : RosterChange(Device);

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
