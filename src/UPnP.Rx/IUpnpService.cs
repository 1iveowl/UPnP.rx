using UPnP.Rx.Model;

namespace UPnP.Rx;

/// <summary>
/// A controllable service on a described device. Implemented by
/// <see cref="UpnpService"/>; exists so consumers can fake a service in their
/// own tests without replaying SSDP/SOAP exchanges.
/// </summary>
public interface IUpnpService
{
    /// <summary>The service entry from the device description document.</summary>
    ServiceDescription Description { get; }

    /// <summary>The service's SCPD, fetched lazily and cached on success.</summary>
    /// <exception cref="UpnpException">The service declares no SCPD URL, the fetch fails, or the document is not parsable.</exception>
    Task<Scpd> GetScpdAsync(CancellationToken ct = default);

    /// <summary>Invokes a SOAP action on the service, returning its out-arguments.</summary>
    /// <exception cref="UpnpActionException">The device answered with a SOAP fault; carries the <see cref="UpnpError"/>.</exception>
    /// <exception cref="UpnpException">The service declares no control URL or service type, the HTTP exchange fails, or the response is unparsable.</exception>
    Task<ActionResult> InvokeAsync(
        string action,
        IReadOnlyDictionary<string, string>? arguments = null,
        CancellationToken ct = default);
}
