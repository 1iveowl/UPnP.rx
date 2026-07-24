namespace UPnP.Rx.Model;

/// <summary>
/// A service entry from a device description document
/// (<c>serviceList/service</c>, UDA 2.0 clause 2). Immutable. URLs are resolved
/// to absolute URIs against the document base; fields the device omitted or
/// botched are left unset (leniency policy).
/// </summary>
public sealed record ServiceDescription
{
    /// <summary>The service type URN (<c>serviceType</c>), e.g. <c>urn:schemas-upnp-org:service:WANIPConnection:2</c>.</summary>
    public string? ServiceType { get; init; }

    /// <summary>The service identifier URN (<c>serviceId</c>), unique within the device.</summary>
    public string? ServiceId { get; init; }

    /// <summary>The URL of the service's SCPD document (<c>SCPDURL</c>), resolved to absolute.</summary>
    public Uri? ScpdUrl { get; init; }

    /// <summary>The URL SOAP action calls are posted to (<c>controlURL</c>), resolved to absolute.</summary>
    public Uri? ControlUrl { get; init; }

    /// <summary>The URL eventing subscriptions are sent to (<c>eventSubURL</c>), resolved to absolute.</summary>
    public Uri? EventSubUrl { get; init; }
}
