using System.Xml;
using System.Xml.Linq;
using UPnP.Rx.Model;
using UPnP.Rx.Parsing;

namespace UPnP.Rx.Eventing;

/// <summary>
/// Pure parser for GENA NOTIFY bodies (UDA 2.0 clause 4.3:
/// <c>e:propertyset/e:property/&lt;variableName&gt;value</c>). Total and
/// lenient: namespace/case-tolerant, recovers from unescaped ampersands, keeps
/// escaped payloads (e.g. AVTransport <c>LastChange</c>) as decoded strings; a
/// body only fails when it contains no property set at all.
/// </summary>
public static class GenaParser
{
    /// <summary>
    /// Parses a NOTIFY body into its evented variables (possibly empty - devices
    /// send empty property sets as keep-alives).
    /// </summary>
    /// <param name="xml">The NOTIFY request body.</param>
    /// <exception cref="ArgumentNullException"><paramref name="xml"/> is null.</exception>
    public static ParseResult<IReadOnlyList<EventedProperty>> ParsePropertySet(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);

        XDocument document;

        try
        {
            document = XDocument.Parse(xml, LoadOptions.None);
        }
        catch (XmlException initial)
        {
            try
            {
                document = XDocument.Parse(XmlLeniency.EscapeBareAmpersands(xml), LoadOptions.None);
            }
            catch (XmlException)
            {
                return ParseResult<IReadOnlyList<EventedProperty>>.Failure(
                    $"The NOTIFY body is not well-formed XML: {initial.Message}");
            }
        }

        var root = document.Root;

        if (root is null)
        {
            return ParseResult<IReadOnlyList<EventedProperty>>.Failure("The NOTIFY body is empty.");
        }

        var propertySet = string.Equals(root.Name.LocalName, "propertyset", StringComparison.OrdinalIgnoreCase)
            ? root
            : XmlLeniency.Child(root, "propertyset");

        if (propertySet is null)
        {
            return ParseResult<IReadOnlyList<EventedProperty>>.Failure(
                "The NOTIFY body contains no propertyset element.");
        }

        // Each e:property should hold exactly one variable element; be lenient
        // and take every child of every property.
        var properties = XmlLeniency.Children(propertySet, "property")
            .SelectMany(property => property.Elements())
            .Select(variable => new EventedProperty(variable.Name.LocalName, variable.Value.Trim()))
            .ToArray();

        return ParseResult<IReadOnlyList<EventedProperty>>.Success(properties);
    }
}
