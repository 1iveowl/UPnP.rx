namespace UPnP.Rx;

/// <summary>
/// A device's boot identity - the thing that changes when it restarts, so that
/// control points know their cached view of it is stale.
/// </summary>
/// <remarks>
/// <para>
/// Two headers carry this, and which one a device uses says which architecture it
/// implements. UDA 1.1 and later send <c>BOOTID.UPNP.ORG</c>, an integer the device
/// increases on every reboot and network change (UDA 2.0 clause 1.2.2). The UPnP 1.0
/// installed base predates that field and sends <c>NLS</c> instead - an opaque
/// "network location signature" carried under a namespace prefix the device
/// negotiates with an <c>OPT</c> header, per RFC 2774's HTTP Extension Framework.
/// <c>NLS</c> is not UDA-normative; it is a widely deployed de facto extension, and
/// it is treated here as advisory rather than authoritative.
/// </para>
/// <para>
/// Both are optional in practice, so all three states are distinguishable: a device
/// may supply a boot id, a signature, or nothing at all. That last case is not an
/// error - it means reboots are undetectable for that device, and
/// <see cref="IsKnown"/> is how a consumer tells "unchanged" from "no evidence".
/// Comparing signatures for equality is the only supported way to ask "did this
/// device restart"; a raw <see cref="BootId"/> comparison silently misses the whole
/// UPnP 1.0 population, whose value is always <see langword="null"/>.
/// </para>
/// </remarks>
/// <param name="BootId">
/// <c>BOOTID.UPNP.ORG</c>, when the device sent a parsable one. Zero is a legal
/// value (UDA 2.0 ranges the field 0 to 2^31-1), which is why absence is
/// <see langword="null"/> rather than 0.
/// </param>
/// <param name="Nls">
/// The UPnP 1.0 <c>NLS</c> signature, when present. Opaque by design: implementations
/// have used both counters and GUID-shaped strings, so it is never parsed as a number
/// and carries no ordering - only equality is meaningful.
/// </param>
public readonly record struct BootSignature(uint? BootId, string? Nls)
{
    /// <summary>A device that announced no boot identity at all; reboots are undetectable for it.</summary>
    public static BootSignature None => default;

    /// <summary>
    /// Whether the device supplied any boot identity. When <see langword="false"/>,
    /// equality with another signature means "still no evidence", not "unchanged".
    /// </summary>
    public bool IsKnown => BootId.HasValue || !string.IsNullOrEmpty(Nls);

    /// <summary>
    /// Whether this signature differs from <paramref name="previous"/> in a way that
    /// evidences a reboot. False when either side is unknown, so a device that never
    /// announces a boot identity is never mistaken for one that keeps restarting.
    /// </summary>
    /// <param name="previous">The signature recorded the last time the device was seen.</param>
    public bool IndicatesRebootSince(BootSignature previous) =>
        IsKnown && previous.IsKnown && this != previous;

    /// <summary>A short, stable rendering for cache keys and logs; <c>"-"</c> when unknown.</summary>
    public override string ToString() =>
        BootId is { } bootId ? bootId.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : !string.IsNullOrEmpty(Nls) ? $"nls:{Nls}"
        : "-";
}
