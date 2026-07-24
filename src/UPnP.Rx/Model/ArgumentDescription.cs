namespace UPnP.Rx.Model;

/// <summary>The direction of a SOAP action argument.</summary>
public enum ArgumentDirection
{
    /// <summary>The document omitted or botched the <c>direction</c> element (leniency: kept, not dropped).</summary>
    Unknown = 0,

    /// <summary>Sent with the request (<c>in</c>).</summary>
    In,

    /// <summary>Returned in the response (<c>out</c>).</summary>
    Out
}

/// <summary>An argument of an SCPD action (<c>argumentList/argument</c>). Immutable.</summary>
public sealed record ArgumentDescription
{
    /// <summary>The argument name (<c>name</c>), as used for the SOAP element.</summary>
    public string? Name { get; init; }

    /// <summary>Whether the argument is sent with the request or returned in the response.</summary>
    public ArgumentDirection Direction { get; init; }

    /// <summary>Whether the argument is flagged as the action's return value (<c>retval</c>).</summary>
    public bool IsReturnValue { get; init; }

    /// <summary>The state variable defining the argument's type (<c>relatedStateVariable</c>).</summary>
    public string? RelatedStateVariable { get; init; }
}
