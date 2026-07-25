namespace UPnP.Rx.Eventing.Av;

/// <summary>
/// One state-variable change decoded from an AV service's <c>LastChange</c>
/// payload (AVTransport / RenderingControl, UPnP AV eventing model): the
/// service events a single <c>LastChange</c> variable whose value is escaped
/// XML carrying the actual per-instance changes.
/// </summary>
/// <param name="InstanceId">The AV instance the change belongs to (0 on almost every real device).</param>
/// <param name="Name">The state variable's name, e.g. <c>TransportState</c> or <c>Volume</c>.</param>
/// <param name="Value">The value as carried; stays a string per the leniency policy.</param>
/// <param name="Channel">The audio channel for channel-scoped variables (<c>Master</c> on volume/mute), null otherwise.</param>
public sealed record AvPropertyChange(int InstanceId, string Name, string Value, string? Channel);
