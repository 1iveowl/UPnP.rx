namespace UPnP.Rx.PortMapping;

/// <summary>
/// A port mapping on an internet gateway. Immutable. (Named <c>Entry</c> to
/// avoid colliding with the <c>UPnP.Rx.PortMapping</c> namespace in consumer code.)
/// </summary>
public sealed record PortMappingEntry
{
    /// <summary>The WAN-side remote host filter; <see langword="null"/> or empty means any remote host (the wildcard).</summary>
    public string? RemoteHost { get; init; }

    /// <summary>The WAN-side port.</summary>
    public required ushort ExternalPort { get; init; }

    /// <summary>The LAN-side port on <see cref="InternalClient"/>.</summary>
    public required ushort InternalPort { get; init; }

    /// <summary>The transport protocol.</summary>
    public required Protocol Protocol { get; init; }

    /// <summary>The LAN-side host receiving the traffic.</summary>
    public string? InternalClient { get; init; }

    /// <summary>Whether the mapping is active.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>The user-facing description stored on the gateway.</summary>
    public string? Description { get; init; }

    /// <summary>The lease duration; <see cref="TimeSpan.Zero"/> means indefinite (until the gateway reboots or forgets).</summary>
    public TimeSpan LeaseDuration { get; init; }
}

/// <summary>What happened to an auto-renewing port mapping lease.</summary>
public enum PortMappingEventKind
{
    /// <summary>A renewal succeeded; the mapping lives on.</summary>
    Renewed,

    /// <summary>A renewal failed; the lease keeps retrying (per-item failure is data, not stream death).</summary>
    RenewalFailed,

    /// <summary>
    /// Renewals have failed for longer than the lease duration — the gateway has
    /// most likely dropped the mapping. Retries continue; a later success emits
    /// <see cref="Renewed"/> again.
    /// </summary>
    Expired
}

/// <summary>A renewal-lifecycle notification from a <see cref="PortMappingLease"/>. Immutable.</summary>
public sealed record PortMappingEvent
{
    /// <summary>What happened.</summary>
    public required PortMappingEventKind Kind { get; init; }

    /// <summary>Failure detail for <see cref="PortMappingEventKind.RenewalFailed"/>, when available.</summary>
    public string? Message { get; init; }
}
