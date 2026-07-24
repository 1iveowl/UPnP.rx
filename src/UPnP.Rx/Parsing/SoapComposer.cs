using System.Xml.Linq;

namespace UPnP.Rx.Parsing;

/// <summary>
/// Pure composer for SOAP 1.1 action requests (UDA 2.0 clause 3.2). Strict in
/// what we send: well-formed envelopes, correct namespaces, all values escaped.
/// </summary>
public static class SoapComposer
{
    private static readonly XNamespace SoapEnvelope = "http://schemas.xmlsoap.org/soap/envelope/";

    /// <summary>
    /// Composes the SOAP 1.1 envelope for an action call, ready to POST to the
    /// service's <c>controlURL</c>.
    /// </summary>
    /// <param name="serviceType">The service type URN, e.g. <c>urn:schemas-upnp-org:service:WANIPConnection:2</c>.</param>
    /// <param name="actionName">The action to invoke.</param>
    /// <param name="arguments">The in-arguments by name, in SCPD declaration order; values are escaped.</param>
    /// <returns>The envelope, including the XML declaration.</returns>
    /// <exception cref="ArgumentException"><paramref name="serviceType"/> or <paramref name="actionName"/> is null or whitespace.</exception>
    public static string ComposeActionRequest(
        string serviceType,
        string actionName,
        IReadOnlyDictionary<string, string>? arguments = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceType);
        ArgumentException.ThrowIfNullOrWhiteSpace(actionName);

        XNamespace service = serviceType;

        // Per UDA 2.0 clause 3.2.1: the action element is qualified by the service
        // type namespace; argument elements are unqualified.
        var action = new XElement(
            service + actionName,
            new XAttribute(XNamespace.Xmlns + "u", serviceType));

        foreach (var (name, value) in arguments ?? Enumerable.Empty<KeyValuePair<string, string>>())
        {
            action.Add(new XElement(name, value));
        }

        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(
                SoapEnvelope + "Envelope",
                new XAttribute(XNamespace.Xmlns + "s", SoapEnvelope),
                new XAttribute(SoapEnvelope + "encodingStyle", "http://schemas.xmlsoap.org/soap/encoding/"),
                new XElement(SoapEnvelope + "Body", action)));

        return $"{document.Declaration}\n{document.ToString(SaveOptions.DisableFormatting)}";
    }

    /// <summary>
    /// The value for the <c>SOAPACTION</c> HTTP header, quoted per UDA 2.0:
    /// <c>"urn:…:service:X:1#Action"</c>.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="serviceType"/> or <paramref name="actionName"/> is null or whitespace.</exception>
    public static string ComposeSoapActionHeader(string serviceType, string actionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceType);
        ArgumentException.ThrowIfNullOrWhiteSpace(actionName);

        return $"\"{serviceType}#{actionName}\"";
    }
}
