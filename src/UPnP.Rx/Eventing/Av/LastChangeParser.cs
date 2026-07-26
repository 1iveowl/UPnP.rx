using System.Xml;
using System.Xml.Linq;
using UPnP.Rx.Model;
using UPnP.Rx.Parsing;

namespace UPnP.Rx.Eventing.Av;

/// <summary>
/// Pure parser for AV <c>LastChange</c> payloads (the UPnP AV eventing model:
/// <c>Event/InstanceID/&lt;Variable val="…"/&gt;</c>). Total and lenient per
/// house policy: namespace- and case-tolerant, recovers from unescaped
/// ampersands, tolerates variables outside an <c>InstanceID</c> wrapper
/// (instance 0 is assumed), values stay strings. A payload only fails when it
/// is not XML at all.
/// </summary>
public static class LastChangeParser
{
    /// <summary>Parses a decoded <c>LastChange</c> value into its per-instance variable changes (possibly empty).</summary>
    /// <param name="xml">The payload, as delivered by <c>PropertyChange.Value</c> (already entity-decoded by NOTIFY parsing).</param>
    /// <exception cref="ArgumentNullException"><paramref name="xml"/> is null.</exception>
    public static ParseResult<IReadOnlyList<AvPropertyChange>> Parse(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);

        if (!XmlLeniency.TryParseWithAmpersandRecovery(xml, out var document, out var initialError))
        {
            return ParseResult<IReadOnlyList<AvPropertyChange>>.Failure(
                $"The LastChange payload is not well-formed XML: {initialError!.Message}");
        }

        if (document.Root is not { } root)
        {
            return ParseResult<IReadOnlyList<AvPropertyChange>>.Failure("The LastChange payload is empty.");
        }

        var changes = new List<AvPropertyChange>();

        foreach (var element in root.Elements())
        {
            if (string.Equals(element.Name.LocalName, "InstanceID", StringComparison.OrdinalIgnoreCase))
            {
                var instanceId = int.TryParse(Attribute(element, "val"), out var id) ? id : 0;

                foreach (var variable in element.Elements())
                {
                    changes.Add(ToChange(variable, instanceId));
                }
            }
            else
            {
                // Sloppy devices skip the InstanceID wrapper - instance 0.
                changes.Add(ToChange(element, instanceId: 0));
            }
        }

        return ParseResult<IReadOnlyList<AvPropertyChange>>.Success(changes);
    }

    private static AvPropertyChange ToChange(XElement variable, int instanceId) => new(
        instanceId,
        variable.Name.LocalName,
        Attribute(variable, "val") ?? variable.Value.Trim(),
        Attribute(variable, "channel"));

    /// <summary>The attribute's value by case-insensitive local name, or null.</summary>
    private static string? Attribute(XElement element, string localName) =>
        element.Attributes()
            .FirstOrDefault(a => string.Equals(a.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase))
            ?.Value;
}
