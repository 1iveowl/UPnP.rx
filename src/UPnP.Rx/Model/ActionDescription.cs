namespace UPnP.Rx.Model;

/// <summary>An action declared in a service's SCPD (<c>actionList/action</c>). Immutable.</summary>
public sealed record ActionDescription
{
    /// <summary>The action name (<c>name</c>), as used in the SOAP call and <c>SOAPACTION</c> header.</summary>
    public string? Name { get; init; }

    /// <summary>The action's arguments (<c>argumentList</c>) in declaration order; empty when it has none.</summary>
    public IReadOnlyList<ArgumentDescription> Arguments { get; init; } = [];
}
