namespace Sample.Dashboard.Client.Models;

/// <summary>SignalR event names shared by the server hub and the WASM client.</summary>
public static class HubEvents
{
    /// <summary>A device appeared or its description changed; payload: <see cref="DeviceDto"/>.</summary>
    public const string DeviceUp = "DeviceUp";

    /// <summary>A device said byebye; payload: the device key.</summary>
    public const string DeviceGone = "DeviceGone";

    /// <summary>The hub path.</summary>
    public const string Path = "/devicehub";

    /// <summary>Hub RPC: fetch a service's SCPD detail (deviceKey, owning UDN, serviceType).</summary>
    public const string GetServiceDetail = "GetServiceDetail";
}
