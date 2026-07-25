namespace Sample.Dashboard.Client.Models;

/// <summary>The internet gateway's identity and WAN state, as shown on the port-mapping page.</summary>
public sealed record GatewayDto(
    string? FriendlyName,
    string? WanServiceType,
    string? ExternalIp,
    string? Status,
    bool IsConnected,
    string? LastError,
    double UptimeSeconds,
    string? LocalAddress);

/// <summary>One row of the gateway's port-mapping table.</summary>
public sealed record PortMappingDto(
    string Protocol,
    ushort ExternalPort,
    ushort InternalPort,
    string? InternalClient,
    bool Enabled,
    string? Description,
    double LeaseSeconds,
    bool HeldByServer);

/// <summary>A renewal-lifecycle event from a server-held lease, broadcast to all browsers.</summary>
public sealed record LeaseEventDto(
    ushort ExternalPort,
    string Protocol,
    string Kind,
    string? Message,
    DateTimeOffset Timestamp);

/// <summary>
/// One GENA event from a watched service, streamed over the hub. AV LastChange
/// payloads arrive pre-decoded as Kind "AvChange" rows with the channel and
/// instance surfaced.
/// </summary>
public sealed record ServiceEventDto(
    string Kind,
    string? Name,
    string? Value,
    uint Seq,
    bool IsInitialState,
    bool IsReplay,
    string? Message,
    string? Channel = null,
    int InstanceId = 0);
