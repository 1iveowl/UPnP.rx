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

    /// <summary>Hub RPC: the gateway's identity + WAN state, or null when none was found.</summary>
    public const string GetGatewayInfo = "GetGatewayInfo";

    /// <summary>Hub RPC: the gateway's current port-mapping table.</summary>
    public const string GetPortMappings = "GetPortMappings";

    /// <summary>Hub RPC: create an auto-renewing mapping held by the server.</summary>
    public const string AddPortMapping = "AddPortMapping";

    /// <summary>Hub RPC: delete a mapping (disposes the held lease when it is ours).</summary>
    public const string DeletePortMapping = "DeletePortMapping";

    /// <summary>Broadcast: a held lease reported a renewal-lifecycle event; payload <see cref="LeaseEventDto"/>.</summary>
    public const string LeaseEvent = "LeaseEvent";

    /// <summary>Hub RPC: invalidate + re-read one device's description; returns an error message or null.</summary>
    public const string RefreshDevice = "RefreshDevice";

    /// <summary>Hub streaming RPC: live GENA events for one service (deviceKey, owning UDN, serviceType).</summary>
    public const string StreamServiceEvents = "StreamServiceEvents";

    /// <summary>Hub RPC: invoke a SOAP action (deviceKey, udn, serviceType, actionName, args); returns <see cref="InvokeResultDto"/>.</summary>
    public const string InvokeAction = "InvokeAction";

    /// <summary>Hub RPC: clear the roster and search the network afresh (resets discovery dedup state).</summary>
    public const string Rescan = "Rescan";

    /// <summary>Broadcast: one SSDP envelope for the device activity timeline; payload <see cref="SsdpActivityDto"/>.</summary>
    public const string SsdpActivity = "SsdpActivity";

    /// <summary>Broadcast: a rescan reset the roster - clients enter rescan mode (stale cards grayed, live watches ended) and reset their list when the first fresh device arrives.</summary>
    public const string RosterReset = "RosterReset";
}
