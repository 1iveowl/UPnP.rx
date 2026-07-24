namespace UPnP.Rx.Model;

/// <summary>
/// A parsed Service Control Protocol Description (SCPD, UDA 2.0 clause 2.5):
/// the actions a service offers and the state variables backing them. Immutable.
/// </summary>
public sealed record Scpd
{
    /// <summary>The architecture version the document declares (<c>specVersion</c>).</summary>
    public SpecVersion? SpecVersion { get; init; }

    /// <summary>The service's actions (<c>actionList</c>); empty when none are declared.</summary>
    public IReadOnlyList<ActionDescription> Actions { get; init; } = [];

    /// <summary>The service's state variables (<c>serviceStateTable</c>); empty when none are declared.</summary>
    public IReadOnlyList<StateVariable> StateVariables { get; init; } = [];
}
