using UPnP.Rx.Model;

namespace UPnP.Rx;

/// <summary>
/// Thrown by the edge classes on contract violations and protocol failures: an
/// unstarted or misconfigured client, an unknown service, an unfetchable or
/// unidentifiable description, a failed HTTP exchange. Parse outcomes in the
/// pure layer are values (<see cref="ParseResult{T}"/>), never exceptions.
/// </summary>
public class UpnpException : Exception
{
    /// <summary>Creates the exception.</summary>
    public UpnpException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public UpnpException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and inner cause.</summary>
    public UpnpException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown by <see cref="UpnpService.InvokeAsync"/> when the device answers with
/// a SOAP fault; carries the device's <see cref="UpnpError"/>.
/// </summary>
/// <remarks>Creates the exception from the fault's UPnP error.</remarks>
public sealed class UpnpActionException(string message, UpnpError error) : UpnpException(message)
{

    /// <summary>The UPnP error the device returned (<c>errorCode</c>/<c>errorDescription</c>).</summary>
    public UpnpError Error { get; } = error;
}
