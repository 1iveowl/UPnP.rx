using UPnP.Rx;
using System.Collections.Frozen;

namespace UPnP.Rx.Model;

/// <summary>
/// The outcome of a successful SOAP action call: the action's out-arguments by
/// name. Immutable. Argument names compare case-insensitively (leniency: devices
/// occasionally return different casing than their SCPD declares).
/// </summary>
public sealed record ActionResult
{
    /// <summary>The out-arguments returned by the action, by name.</summary>
    public IReadOnlyDictionary<string, string> Out { get; init; } =
        FrozenDictionary<string, string>.Empty;

    /// <summary>
    /// What the response's <c>SERVER</c> header claimed about the device's UDA
    /// version, when it sent a parsable one. UDA 2.0 clause 1 notes that SERVER is
    /// "also used in control and eventing to communicate which version of UPnP
    /// networking the devices... support", which makes this an independent witness -
    /// it is often produced by different firmware than the description document, so
    /// it can contradict it.
    /// </summary>
    public UpnpVersionClaims VersionClaims { get; init; } = UpnpVersionClaims.None;

    /// <summary>The named out-argument, or <see langword="null"/> when the action did not return it.</summary>
    public string? this[string name] => Out.GetValueOrDefault(name);
}
