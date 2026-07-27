namespace Sample.Dashboard.Client.Models;

/// <summary>
/// The wire shape shared between the server (which does the SSDP listening) and
/// the WebAssembly client (which only ever sees the SignalR stream). The full
/// device tree travels so the client can offer drill-down without another
/// round trip.
/// </summary>
public sealed record DeviceDto(
    string Key,
    string? FriendlyName,
    string? Manufacturer,
    string? Model,
    string Location,
    int ServiceCount,
    int DeviceCount,
    DeviceNodeDto Root,
    UpnpVersionClaimDto[] VersionClaims);

/// <summary>
/// One device statement about the UDA version it implements, with the source that
/// stated it. UDA 2.0 makes several sources normative and names no authority
/// between them, so the browser shows each claim rather than a reconciled number -
/// and flags the device when they disagree.
/// </summary>
public sealed record UpnpVersionClaimDto(string Source, string Version, string? Detail);

/// <summary>One device in the tree: its own services plus embedded children.</summary>
public sealed record DeviceNodeDto(
    string? FriendlyName,
    string? DeviceType,
    string? Manufacturer,
    string? Model,
    string? Udn,
    string[] Services,
    DeviceNodeDto[] Children);

/// <summary>
/// Why a device is no longer on the network, and when the dashboard noticed. The
/// timestamp is the browser's own clock at the moment the departure arrived - no
/// device reports when it left, and its last announcement can be a whole max-age
/// older than the departure itself.
/// </summary>
/// <param name="Reason">
/// <c>left</c> when the device sent <c>ssdp:byebye</c> - it announced its departure,
/// so "off" is honest. <c>expired</c> when its advertisement simply lapsed, which is
/// all a device dropping to standby without a goodbye tells us.
/// </param>
/// <param name="At">When the dashboard noticed.</param>
public sealed record DepartureDto(string Reason, DateTimeOffset At);

/// <summary>
/// The two ways a device leaves the roster, as they travel over the hub. Shared so
/// the server that produces one and the browser that reads it cannot drift apart.
/// </summary>
public static class DepartureReasons
{
    /// <summary>The device sent <c>ssdp:byebye</c> - it announced that it was going.</summary>
    public const string Left = "left";

    /// <summary>Its advertisement lapsed without a goodbye; off, asleep or out of range are all consistent.</summary>
    public const string Expired = "expired";
}
