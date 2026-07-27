using UPnP.Rx.Model;

namespace UPnP.Rx;

/// <summary>Where a device stated the UDA version it implements.</summary>
public enum UpnpVersionSource
{
    /// <summary>The <c>SERVER</c> header on a discovery message (known before anything is fetched).</summary>
    Server,

    /// <summary>The device description's <c>&lt;specVersion&gt;</c> (UDA 2.0 clause 2.3).</summary>
    DeviceDescription,

    /// <summary>One service's SCPD <c>&lt;specVersion&gt;</c> (UDA 2.0 clause 2.5).</summary>
    ServiceDescription,

    /// <summary>The <c>SERVER</c> header on a control (SOAP) response.</summary>
    ControlResponse
}

/// <summary>One statement by a device about the UDA version it implements.</summary>
/// <param name="Source">Which document or header carried the claim.</param>
/// <param name="Version">The architecture version claimed.</param>
/// <param name="Detail">
/// What within the source stated it - a service id for
/// <see cref="UpnpVersionSource.ServiceDescription"/>, an action name for
/// <see cref="UpnpVersionSource.ControlResponse"/>. Null when the source is the device itself.
/// </param>
public sealed record UpnpVersionClaim(UpnpVersionSource Source, Version Version, string? Detail = null);

/// <summary>
/// Every version claim gathered for a device, kept separate rather than collapsed,
/// plus a conservative reconciliation.
/// </summary>
/// <remarks>
/// <para>
/// UDA 2.0 states the architecture version in more than one place and makes each
/// one normative: the <c>SERVER</c> header's second product token "shall be UPnP/2.0"
/// (stated identically in clauses 1.2.2, 1.3.3 and 3.2.2), and
/// the description's <c>&lt;specVersion&gt;</c> "defines the architecture on which
/// the device is implemented", with the minor element required to "accurately
/// reflect the version number of the UPnP Device Architecture supported by the
/// device" (clause 2.3). The spec calls these "the same information" and never
/// contemplates them disagreeing, so it names <b>no authority</b> between them.
/// </para>
/// <para>
/// Because the spec is silent, this type does not pick a winner and hide the rest -
/// it keeps every claim with its provenance, and a disagreement
/// (<see cref="SourcesAgree"/> false) is reportable as what it is: the device
/// violating one of two "shall" clauses.
/// </para>
/// <para>
/// <see cref="Effective"/> is a deliberate engineering tie-break, not a spec rule.
/// It takes the <i>lowest</i> claim, because the costs are asymmetric: over-claiming
/// means relying on features a device may not implement (<c>BOOTID</c>,
/// <c>CONFIGID</c>, <c>SEARCHPORT</c>, <c>ssdp:update</c>), while under-claiming only
/// forgoes a capability. Below 1.1 an absent <c>BOOTID</c> is expected rather than a
/// defect - see <see cref="BootSignature"/>.
/// </para>
/// </remarks>
public sealed record UpnpVersionClaims
{
    /// <summary>Nothing was claimed - the device stated no version anywhere yet seen.</summary>
    public static UpnpVersionClaims None { get; } = new([]);

    /// <summary>Creates a set of claims.</summary>
    /// <param name="claims">The claims gathered so far; order is preserved.</param>
    /// <exception cref="ArgumentNullException"><paramref name="claims"/> is null.</exception>
    public UpnpVersionClaims(IReadOnlyList<UpnpVersionClaim> claims)
    {
        ArgumentNullException.ThrowIfNull(claims);
        Claims = claims;
    }

    /// <summary>Every claim, with the source that stated it.</summary>
    public IReadOnlyList<UpnpVersionClaim> Claims { get; }

    /// <summary>
    /// The version to act on: the lowest claimed, or <see langword="null"/> when
    /// nothing has been claimed. A tie-break of this library's choosing - see the
    /// type's remarks - never a spec requirement.
    /// </summary>
    public Version? Effective => Claims.Count == 0 ? null : Claims.Min(claim => claim.Version);

    /// <summary>
    /// Whether every source agrees. Vacuously <see langword="true"/> with fewer than
    /// two claims. When false, the device contradicts itself and both readings are in
    /// <see cref="Claims"/>.
    /// </summary>
    public bool SourcesAgree => Claims.DistinctBy(claim => claim.Version).Count() <= 1;

    /// <summary>Adds claims, returning a new set (claims arrive at different times - SCPDs and control responses are late).</summary>
    /// <param name="additional">The claims to fold in.</param>
    /// <exception cref="ArgumentNullException"><paramref name="additional"/> is null.</exception>
    public UpnpVersionClaims With(params UpnpVersionClaim[] additional)
    {
        ArgumentNullException.ThrowIfNull(additional);
        return additional.Length == 0 ? this : new UpnpVersionClaims([.. Claims, .. additional]);
    }

    /// <summary>Merges another set of claims into this one.</summary>
    /// <param name="other">The claims to fold in.</param>
    /// <exception cref="ArgumentNullException"><paramref name="other"/> is null.</exception>
    public UpnpVersionClaims With(UpnpVersionClaims other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return other.Claims.Count == 0 ? this : new UpnpVersionClaims([.. Claims, .. other.Claims]);
    }

    internal static Version? ToVersion(SpecVersion? specVersion) =>
        specVersion is null ? null : new Version(specVersion.Major, specVersion.Minor);

    internal static Version? ToVersion(SSDP.UPnP.PCL.Model.DeviceInfo? deviceInfo) =>
        deviceInfo?.UpnpMajorVersion is { } major && deviceInfo.UpnpMinorVersion is { } minor
            ? new Version(major, minor)
            : null;

    internal static UpnpVersionClaims From(UpnpVersionSource source, Version? version, string? detail = null) =>
        version is null ? None : new UpnpVersionClaims([new UpnpVersionClaim(source, version, detail)]);
}
