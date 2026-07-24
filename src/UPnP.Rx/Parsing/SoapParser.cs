using System.Collections.Frozen;
using System.Xml;
using System.Xml.Linq;
using UPnP.Rx.Model;

namespace UPnP.Rx.Parsing;

/// <summary>
/// Pure parser for SOAP 1.1 action responses and faults (UDA 2.0 clause 3.2).
/// Total and lenient: namespace- and case-tolerant, recovers from unescaped
/// ampersands; failures are values, never exceptions.
/// </summary>
public static class SoapParser
{
    /// <summary>
    /// Parses a successful action response into the action's out-arguments.
    /// </summary>
    /// <param name="xml">The HTTP response body.</param>
    /// <param name="actionName">The action that was invoked; its response element is <c>[actionName]Response</c>.</param>
    /// <returns>
    /// The out-arguments (possibly empty), or a failure when the body is not XML,
    /// is a SOAP fault (parse it with <see cref="ParseFault"/>), or contains no
    /// response element.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="xml"/> or <paramref name="actionName"/> is null.</exception>
    public static ParseResult<ActionResult> ParseActionResponse(string xml, string actionName)
    {
        ArgumentNullException.ThrowIfNull(xml);
        ArgumentNullException.ThrowIfNull(actionName);

        if (TryParse(xml, out var document, out var error))
        {
            var body = FindBody(document);

            if (body is null)
            {
                return ParseResult<ActionResult>.Failure("The document contains no SOAP Body element.");
            }

            if (FindFault(body) is not null)
            {
                return ParseResult<ActionResult>.Failure(
                    "The response is a SOAP fault; parse it with ParseFault.");
            }

            // Exact [actionName]Response first; fall back to any *Response element
            // (devices have been seen answering with the wrong action name).
            var response =
                body.Elements().FirstOrDefault(e => string.Equals(
                    e.Name.LocalName, $"{actionName}Response", StringComparison.OrdinalIgnoreCase))
                ?? body.Elements().FirstOrDefault(e => e.Name.LocalName.EndsWith(
                    "Response", StringComparison.OrdinalIgnoreCase));

            if (response is null)
            {
                return ParseResult<ActionResult>.Failure(
                    $"The SOAP Body contains no {actionName}Response element.");
            }

            return ParseResult<ActionResult>.Success(new ActionResult
            {
                Out = response.Elements()
                    .Where(e => e.Name.LocalName.Length > 0)
                    .ToFrozenDictionary(
                        e => e.Name.LocalName,
                        e => e.Value.Trim(),
                        StringComparer.OrdinalIgnoreCase)
            });
        }

        return ParseResult<ActionResult>.Failure(error);
    }

    /// <summary>
    /// Parses a SOAP fault body into the UPnP error it carries
    /// (<c>detail/UPnPError/errorCode</c>, UDA 2.0 clause 3.2.2).
    /// </summary>
    /// <param name="xml">The HTTP response body (typically served with status 500).</param>
    /// <returns>The UPnP error, or a failure when the body carries no parsable <c>errorCode</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="xml"/> is null.</exception>
    public static ParseResult<UpnpError> ParseFault(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);

        if (!TryParse(xml, out var document, out var error))
        {
            return ParseResult<UpnpError>.Failure(error);
        }

        // Search the whole document rather than the strict
        // Body/Fault/detail/UPnPError path — devices nest the error sloppily.
        var upnpError = document
            .Descendants()
            .FirstOrDefault(e => string.Equals(e.Name.LocalName, "UPnPError", StringComparison.OrdinalIgnoreCase));

        if (upnpError is null)
        {
            return ParseResult<UpnpError>.Failure("The document contains no UPnPError element.");
        }

        var code = XmlLeniency.Int(upnpError, "errorCode");

        if (code is null)
        {
            return ParseResult<UpnpError>.Failure("The UPnPError element carries no parsable errorCode.");
        }

        return ParseResult<UpnpError>.Success(new UpnpError
        {
            Code = code.Value,
            Description = XmlLeniency.Text(upnpError, "errorDescription")
        });
    }

    private static bool TryParse(string xml, out XDocument document, out string error)
    {
        try
        {
            document = XDocument.Parse(xml, LoadOptions.None);
            error = string.Empty;
            return true;
        }
        catch (XmlException initial)
        {
            try
            {
                document = XDocument.Parse(XmlLeniency.EscapeBareAmpersands(xml), LoadOptions.None);
                error = string.Empty;
                return true;
            }
            catch (XmlException)
            {
                document = new XDocument();
                error = $"The document is not well-formed XML: {initial.Message}";
                return false;
            }
        }
    }

    private static XElement? FindBody(XDocument document) =>
        document.Root is { } root && string.Equals(root.Name.LocalName, "Body", StringComparison.OrdinalIgnoreCase)
            ? root
            : document.Root is { } envelope
                ? XmlLeniency.Child(envelope, "Body")
                : null;

    private static XElement? FindFault(XElement body) =>
        XmlLeniency.Child(body, "Fault");
}
