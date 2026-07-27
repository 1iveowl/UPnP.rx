namespace UPnP.Rx.Eventing;

/// <summary>
/// What a subscription needs to know about the device behind its event endpoint:
/// which device presence notices refer to, and which configuration it subscribed
/// against.
/// </summary>
/// <param name="Udn">
/// The owning device's UDN, used to match presence changes. Null when the
/// description declared none, which leaves the subscription unable to notice a
/// cancellation - it then falls back to renewal failure, as before.
/// </param>
/// <param name="ConfigId">
/// The <c>CONFIGID</c> of the description this subscription's <c>eventSubURL</c>
/// came from. UDA 2.0 clause 1.2.2 guarantees that two messages carrying the same
/// CONFIGID describe the same configuration - which includes the device description
/// and every SCPD - so an unchanged value across a reboot is what makes the cached
/// URL safe to reuse.
/// </param>
internal sealed record DeviceIdentity(string? Udn, int? ConfigId)
{
    /// <summary>
    /// The bare UUID, with the <c>uuid:</c> prefix a description's <c>UDN</c> carries
    /// stripped. UDA 2.0 clause 1.1.2 says the USN prefix "shall match the value of
    /// the UDN element in the device description", but the SSDP layer reports that
    /// prefix already stripped - so the two spellings of the same identity have to be
    /// normalised before they can be compared, or nothing ever matches.
    /// </summary>
    public string? Uuid { get; } =
        Udn is null ? null
        : Udn.StartsWith("uuid:", StringComparison.OrdinalIgnoreCase) ? Udn["uuid:".Length..]
        : Udn;
}
