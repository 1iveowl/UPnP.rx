namespace UPnP.Rx.Model;

/// <summary>
/// The UPnP error carried in a SOAP fault's <c>detail/UPnPError</c> element
/// (UDA 2.0 clause 3.2.2): 401 Invalid Action, 402 Invalid Args, 501 Action
/// Failed, 6xx action-specific codes. Immutable.
/// </summary>
public sealed record UpnpError
{
    /// <summary>The numeric error code (<c>errorCode</c>).</summary>
    public int Code { get; init; }

    /// <summary>The human-readable error description (<c>errorDescription</c>), if the device sent one.</summary>
    public string? Description { get; init; }
}
