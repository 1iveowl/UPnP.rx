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
