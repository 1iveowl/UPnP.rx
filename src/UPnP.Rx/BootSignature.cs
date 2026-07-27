namespace UPnP.Rx;

/// <summary>
/// A device's boot identity - the thing that changes when it restarts, so that
/// control points know their cached view of it is stale.
/// </summary>
/// <remarks>
/// <para>
/// Two headers carry this. UDA 1.1 and later send <c>BOOTID.UPNP.ORG</c>, an integer
/// the device increases on every reboot and network-configuration change (UDA 2.0
/// clause 1.2.2). Alongside it, <c>NLS</c> - a "network location signature" carried
/// under a namespace prefix the device negotiates with an <c>OPT</c> header, per
/// RFC 2774's HTTP Extension Framework - is what the UPnP 1.0 installed base sends
/// instead, and what UDA 2.0's <b>normative</b> Annex A.4.1 still requires of IPv6
/// devices: they "SHALL include an OPT header field and NLS header field", and
/// control points "SHALL recognize" them. Its value changes when the device's network
/// configuration changes, which is a broader trigger than a reboot.
/// </para>
/// <para>
/// Both are optional in practice, so all three states are distinguishable: a device
/// may supply a boot id, a signature, or nothing at all. That last case is not an
/// error - it means reboots are undetectable for that device, and
/// <see cref="IsKnown"/> is how a consumer tells "unchanged" from "no evidence".
/// <see cref="IndicatesRebootSince"/> is the supported way to ask "did this device
/// restart": a raw <see cref="BootId"/> comparison silently misses the whole UPnP 1.0
/// population, whose value is always <see langword="null"/>, while a naive equality
/// check over both fields reports a restart when a device merely stops echoing NLS -
/// which Annex A.4.2 permits, since NLS rides IPv6 advertisements but need not appear
/// on the paired IPv4 ones.
/// </para>
/// </remarks>
/// <param name="BootId">
/// <c>BOOTID.UPNP.ORG</c>, when the device sent a parsable one. Zero is a legal
/// value (UDA 2.0 ranges the field 0 to 2^31-1), which is why absence is
/// <see langword="null"/> rather than 0.
/// </param>
/// <param name="Nls">
/// The <c>NLS</c> signature, when present. Opaque by design: Annex A.4.1 recommends a
/// GUID but permits anything of 1 to 64 characters, and explicitly allows a device to
/// set it equal to its own BOOTID. So it is never parsed as a number and carries no
/// ordering - only equality is meaningful.
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
    public bool IndicatesRebootSince(BootSignature previous)
    {
        // Compare like with like. A device may carry NLS on one announcement and not
        // the next (Annex A.4.2 requires it on IPv6 advertisements but not the paired
        // IPv4 ones) and may set NLS equal to its BOOTID (A.4.1), so comparing the
        // union would read a presentation difference as a restart.
        if (BootId is { } current && previous.BootId is { } prior)
        {
            // Clause 1.2.2: the same BOOTID shall be used in every message for as long
            // as the device stays available, so an equal BOOTID settles it.
            return current != prior;
        }

        if (Nls is { Length: > 0 } currentNls && previous.Nls is { Length: > 0 } priorNls)
        {
            return !string.Equals(currentNls, priorNls, StringComparison.Ordinal);
        }

        return false;               // nothing comparable on both sides: no evidence
    }

    /// <summary>A short, stable rendering for cache keys and logs; <c>"-"</c> when unknown.</summary>
    public override string ToString() =>
        BootId is { } bootId ? bootId.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : !string.IsNullOrEmpty(Nls) ? $"nls:{Nls}"
        : "-";
}
