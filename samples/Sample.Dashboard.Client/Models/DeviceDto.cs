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
