using System.Xml;
using System.Xml.Linq;
using UPnP.Rx.Model;

namespace UPnP.Rx.Parsing;

/// <summary>
/// Pure parser for device description documents (DDD, UDA 2.0 clause 2).
/// Total: bad input yields a failed <see cref="ParseResult{T}"/>, never an
/// exception. Lenient: namespace- and case-tolerant lookups, recovery from
/// unescaped ampersands, unset fields for anything unparsable — a document only
/// fails when it identifies no device at all.
/// </summary>
public static class DescriptionParser
{
    /// <summary>
    /// Parses a device description document into a <see cref="DeviceDescription"/> tree.
    /// </summary>
    /// <param name="xml">The document body as fetched from the device.</param>
    /// <param name="location">
    /// The absolute URL the document was fetched from (the SSDP <c>LOCATION</c>);
    /// relative URLs resolve against it per UDA 2.0 — unless the document carries
    /// a UDA 1.0-era <c>URLBase</c>, which is honored when present (leniency).
    /// </param>
    /// <returns>The parsed device tree, or a failure when the document is not XML or contains no device element.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="xml"/> or <paramref name="location"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="location"/> is not an absolute URI.</exception>
    public static ParseResult<DeviceDescription> ParseDeviceDescription(string xml, Uri location)
    {
        ArgumentNullException.ThrowIfNull(xml);
        ArgumentNullException.ThrowIfNull(location);

        if (!location.IsAbsoluteUri)
        {
            throw new ArgumentException("The location must be an absolute URI.", nameof(location));
        }

        if (!TryParseWithAmpersandRecovery(xml, out var document, out var initialError))
        {
            return ParseResult<DeviceDescription>.Failure(
                $"The document is not well-formed XML: {initialError!.Message}");
        }

        var root = document.Root;

        if (root is null)
        {
            return ParseResult<DeviceDescription>.Failure("The document is empty.");
        }

        var deviceElement = string.Equals(root.Name.LocalName, "device", StringComparison.OrdinalIgnoreCase)
            ? root
            : XmlLeniency.Child(root, "device");

        if (deviceElement is null)
        {
            return ParseResult<DeviceDescription>.Failure("The document contains no device element.");
        }

        var baseUrl = ResolveBaseUrl(root, location);
        var specVersion = ParseSpecVersion(root);
        var configId = ParseConfigId(root);

        return ParseResult<DeviceDescription>.Success(
            ParseDevice(deviceElement, location, baseUrl, specVersion, configId, depth: 0));
    }

    /// <summary>
    /// Embedded-device recursion cap. Real trees are ~3 deep (IGD); a hostile or
    /// absurdly broken document must not be able to overflow the stack — deeper
    /// nesting is truncated, keeping the parser total.
    /// </summary>
    private const int _maxDeviceDepth = 16;

    private static XDocument Parse(string xml) => XDocument.Parse(xml, LoadOptions.None);

    private static bool TryParseWithAmpersandRecovery(
        string xml,
        out XDocument document,
        out XmlException? initialError)
    {
        try
        {
            document = Parse(xml);
            initialError = null;
            return true;
        }
        catch (XmlException error)
        {
            initialError = error;
        }

        try
        {
            // Most common real-world malformation: unescaped '&' in text.
            document = Parse(XmlLeniency.EscapeBareAmpersands(xml));
            return true;
        }
        catch (XmlException)
        {
            document = null!;
            return false;
        }
    }

    /// <summary>
    /// UDA 2.0 resolves against LOCATION; a UDA 1.0-era <c>URLBase</c> wins when a
    /// device ships a usable (absolute http/https) one. A relative or non-HTTP
    /// URLBase is ignored — on Unix, <c>Uri.TryCreate("/x", Absolute)</c> would
    /// otherwise succeed as <c>file:///x</c> and poison every resolved URL.
    /// </summary>
    private static Uri ResolveBaseUrl(XElement root, Uri location) =>
        Uri.TryCreate(XmlLeniency.Token(root, "URLBase"), UriKind.Absolute, out var urlBase)
        && urlBase.Scheme is "http" or "https"
            ? urlBase
            : location;

    private static SpecVersion? ParseSpecVersion(XElement root)
    {
        var specVersion = XmlLeniency.Child(root, "specVersion");

        if (specVersion is null)
        {
            return null;
        }

        var major = XmlLeniency.Int(specVersion, "major");

        return major is null
            ? null
            : new SpecVersion { Major = major.Value, Minor = XmlLeniency.Int(specVersion, "minor") ?? 0 };
    }

    private static int? ParseConfigId(XElement root)
    {
        var attribute = root.Attributes().FirstOrDefault(a =>
            string.Equals(a.Name.LocalName, "configId", StringComparison.OrdinalIgnoreCase));

        return int.TryParse(attribute?.Value.Trim(), out var configId) ? configId : null;
    }

    private static DeviceDescription ParseDevice(
        XElement device,
        Uri location,
        Uri baseUrl,
        SpecVersion? specVersion,
        int? configId,
        int depth)
    {
        var serviceList = XmlLeniency.Child(device, "serviceList");
        var deviceList = XmlLeniency.Child(device, "deviceList");
        var iconList = XmlLeniency.Child(device, "iconList");

        return new DeviceDescription
        {
            Location = location,
            BaseUrl = baseUrl,
            SpecVersion = specVersion,
            ConfigId = configId,
            DeviceType = XmlLeniency.Token(device, "deviceType"),
            FriendlyName = XmlLeniency.Text(device, "friendlyName"),
            Udn = XmlLeniency.Token(device, "UDN"),
            Manufacturer = XmlLeniency.Text(device, "manufacturer"),
            ManufacturerUrl = XmlLeniency.Text(device, "manufacturerURL"),
            ModelDescription = XmlLeniency.Text(device, "modelDescription"),
            ModelName = XmlLeniency.Text(device, "modelName"),
            ModelNumber = XmlLeniency.Text(device, "modelNumber"),
            ModelUrl = XmlLeniency.Text(device, "modelURL"),
            SerialNumber = XmlLeniency.Text(device, "serialNumber"),
            Upc = XmlLeniency.Text(device, "UPC"),
            PresentationUrl = XmlLeniency.AbsoluteUri(baseUrl, XmlLeniency.Token(device, "presentationURL")),
            Icons = iconList is null
                ? []
                : [.. XmlLeniency.Children(iconList, "icon").Select(icon => ParseIcon(icon, baseUrl))],
            Services = serviceList is null
                ? []
                : [.. XmlLeniency.Children(serviceList, "service").Select(service => ParseService(service, baseUrl))],
            EmbeddedDevices = deviceList is null || depth >= _maxDeviceDepth
                ? []
                : [.. XmlLeniency.Children(deviceList, "device")
                        .Select(embedded => ParseDevice(embedded, location, baseUrl, specVersion, configId, depth + 1))]
        };
    }

    private static ServiceDescription ParseService(XElement service, Uri baseUrl) => new()
    {
        ServiceType = XmlLeniency.Token(service, "serviceType"),
        ServiceId = XmlLeniency.Token(service, "serviceId"),
        ScpdUrl = XmlLeniency.AbsoluteUri(baseUrl, XmlLeniency.Token(service, "SCPDURL")),
        ControlUrl = XmlLeniency.AbsoluteUri(baseUrl, XmlLeniency.Token(service, "controlURL")),
        EventSubUrl = XmlLeniency.AbsoluteUri(baseUrl, XmlLeniency.Token(service, "eventSubURL"))
    };

    private static IconDescription ParseIcon(XElement icon, Uri baseUrl) => new()
    {
        MimeType = XmlLeniency.Text(icon, "mimetype"),
        Width = XmlLeniency.Int(icon, "width"),
        Height = XmlLeniency.Int(icon, "height"),
        Depth = XmlLeniency.Int(icon, "depth"),
        Url = XmlLeniency.AbsoluteUri(baseUrl, XmlLeniency.Token(icon, "url"))
    };
}
