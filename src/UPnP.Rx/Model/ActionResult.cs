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

    /// <summary>The named out-argument, or <see langword="null"/> when the action did not return it.</summary>
    public string? this[string name] => Out.GetValueOrDefault(name);
}
