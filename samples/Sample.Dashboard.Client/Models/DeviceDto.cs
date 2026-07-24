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
    DeviceNodeDto Root);

/// <summary>One device in the tree: its own services plus embedded children.</summary>
public sealed record DeviceNodeDto(
    string? FriendlyName,
    string? DeviceType,
    string? Manufacturer,
    string? Model,
    string? Udn,
    string[] Services,
    DeviceNodeDto[] Children);
