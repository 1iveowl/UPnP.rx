using SSDP.UPnP.PCL.Model;

namespace UPnP.Rx;

/// <summary>
/// Convenience factories for the common SSDP search targets (decision 6) — thin
/// sugar over <see cref="ST"/>/<see cref="STType"/>; no URN string-building.
/// </summary>
public static class SearchTargets
{
    /// <summary>
    /// <c>upnp:rootdevice</c> — one response per device; its description document
    /// enumerates embedded devices and services. The out-of-box default.
    /// </summary>
    public static ST RootDevice { get; } = new() { StSearchType = STType.RootDeviceSearch };

    /// <summary><c>ssdp:all</c> — every device and service announcement; chatty on busy networks.</summary>
    public static ST All { get; } = new() { StSearchType = STType.All };

    /// <summary>Search for a standard device type, e.g. <c>DeviceType("InternetGatewayDevice", 2)</c>.</summary>
    public static ST DeviceType(string typeName, int version = 1) => new()
    {
        StSearchType = STType.DeviceTypeSearch,
        TypeName = typeName,
        Version = version
    };

    /// <summary>Search for a standard service type, e.g. <c>ServiceType("WANIPConnection", 1)</c>.</summary>
    public static ST ServiceType(string typeName, int version = 1) => new()
    {
        StSearchType = STType.ServiceTypeSearch,
        TypeName = typeName,
        Version = version
    };

    /// <summary>Search for one specific device by UUID.</summary>
    public static ST Uuid(string deviceUuid) => new()
    {
        StSearchType = STType.UuidSearch,
        DeviceUUID = deviceUuid
    };
}
