namespace Sample.Dashboard.Client.Models;

/// <summary>
/// The wire shape shared between the server (which does the SSDP listening) and
/// the WebAssembly client (which only ever sees the SignalR stream).
/// </summary>
public sealed record DeviceDto(
    string Key,
    string? FriendlyName,
    string? DeviceType,
    string? Manufacturer,
    string? Model,
    string Location,
    string[] Services,
    int DeviceCount);
