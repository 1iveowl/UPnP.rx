namespace UPnP.Rx.PortMapping;

/// <summary>
/// A port mapping on an internet gateway. Immutable. (Named <c>Entry</c> to
/// avoid colliding with the <c>UPnP.Rx.PortMapping</c> namespace in consumer code.)
/// </summary>
/// <remarks>
/// The nullable members carry the leniency policy into the read-back path: a gateway
/// that omits a field, or answers with something unparsable, leaves it <see langword="null"/>
/// rather than being reported as a specific value it never sent. That distinction is
/// load-bearing for <see cref="LeaseDuration"/>, where the obvious sentinel
/// (<see cref="TimeSpan.Zero"/>) already means "never expires".
/// </remarks>
public sealed record PortMappingEntry
{
    /// <summary>The WAN-side remote host filter; <see langword="null"/> or empty means any remote host (the wildcard).</summary>
    public string? RemoteHost { get; init; }

    /// <summary>The WAN-side port.</summary>
    public required ushort ExternalPort { get; init; }

    /// <summary>
    /// The LAN-side port on <see cref="InternalClient"/>, or <see langword="null"/>
    /// when the gateway did not report a usable one.
    /// </summary>
    public required ushort? InternalPort { get; init; }

    /// <summary>The transport protocol.</summary>
    public required Protocol Protocol { get; init; }

    /// <summary>The LAN-side host receiving the traffic.</summary>
    public string? InternalClient { get; init; }

    /// <summary>Whether the mapping is active.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>The user-facing description stored on the gateway.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// The lease duration. <see cref="TimeSpan.Zero"/> means indefinite - the mapping
    /// stays until the gateway reboots or forgets it - and <see langword="null"/> means
    /// the gateway reported no usable value, which is a different statement and must not
    /// be read as "indefinite".
    /// </summary>
    /// <remarks>
    /// IGD carries this as a <c>ui4</c> of seconds, ranged 0-604800 by the standardized
    /// service template. Values outside that range are refused here rather than composed:
    /// a negative <see cref="TimeSpan"/> used to saturate through the <c>(uint)</c>
    /// conversion to 0 and ask the gateway for a <em>permanent</em> mapping - the exact
    /// opposite of a short one, silently. The <c>UPNPRX001</c> analyzer reports the same
    /// thing at build time for literals.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative or above 604800 seconds.</exception>
    public TimeSpan? LeaseDuration
    {
        get;
        init => field = LeaseDurations.Validated(value);
    }
}

/// <summary>The IGD lease-duration range, and the guard that keeps values inside it.</summary>
public static class LeaseDurations
{
    /// <summary>
    /// The longest lease IGD's standardized service template allows: 604 800 seconds
    /// (7 days). <c>PortMappingLeaseDuration</c> is declared <c>ui4</c> with
    /// <c>allowedValueRange</c> 0-604800.
    /// </summary>
    public static TimeSpan Maximum { get; } = TimeSpan.FromSeconds(604_800);

    /// <summary>An explicitly permanent mapping - IGD encodes it as a lease of zero.</summary>
    public static TimeSpan Indefinite => TimeSpan.Zero;

    /// <summary>Whether <paramref name="lease"/> is one IGD can carry.</summary>
    /// <param name="lease">The lease to check; <see langword="null"/> (unknown) is valid.</param>
    public static bool IsValid(TimeSpan? lease) =>
        lease is not { } value || (value >= TimeSpan.Zero && value <= Maximum);

    /// <summary>Returns <paramref name="lease"/> when IGD can carry it, and throws otherwise.</summary>
    /// <param name="lease">The lease to validate.</param>
    /// <param name="name">The parameter name to report; supplied by the compiler.</param>
    /// <returns>The validated lease.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative or above <see cref="Maximum"/>.</exception>
    public static TimeSpan? Validated(
        TimeSpan? lease,
        [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(lease))] string? name = null) =>
        IsValid(lease)
            ? lease
            : throw new ArgumentOutOfRangeException(
                name,
                lease,
                $"An IGD lease duration must be between zero and {Maximum.TotalSeconds:F0} seconds "
                + "(zero means indefinite). A negative lease would be sent as zero, asking for a permanent mapping.");
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
