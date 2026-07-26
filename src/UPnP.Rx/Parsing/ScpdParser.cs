using System.Xml;
using System.Xml.Linq;
using UPnP.Rx.Model;

namespace UPnP.Rx.Parsing;

/// <summary>
/// Pure parser for Service Control Protocol Descriptions (SCPD, UDA 2.0
/// clause 2.5). Total: bad input yields a failed <see cref="ParseResult{T}"/>,
/// never an exception. Lenient: namespace- and case-tolerant, recovers from
/// unescaped ampersands, keeps declarations with missing optional fields.
/// </summary>
public static class ScpdParser
{
    /// <summary>Parses an SCPD document into a <see cref="Scpd"/>.</summary>
    /// <param name="xml">The document body as fetched from the service's <c>SCPDURL</c>.</param>
    /// <returns>The parsed SCPD, or a failure when the input is not XML at all.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="xml"/> is null.</exception>
    public static ParseResult<Scpd> ParseScpd(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);

        if (!XmlLeniency.TryParseWithAmpersandRecovery(xml, out var document, out var initialError))
        {
            return ParseResult<Scpd>.Failure($"The document is not well-formed XML: {initialError!.Message}");
        }

        var root = document.Root;

        if (root is null)
        {
            return ParseResult<Scpd>.Failure("The document is empty.");
        }

        var actionList = XmlLeniency.Child(root, "actionList");
        var stateTable = XmlLeniency.Child(root, "serviceStateTable");

        return ParseResult<Scpd>.Success(new Scpd
        {
            SpecVersion = ParseSpecVersion(root),
            Actions = actionList is null
                ? []
                : [.. XmlLeniency.Children(actionList, "action").Select(ParseAction)],
            StateVariables = stateTable is null
                ? []
                : [.. XmlLeniency.Children(stateTable, "stateVariable").Select(ParseStateVariable)]
        });
    }

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

    private static ActionDescription ParseAction(XElement action)
    {
        var argumentList = XmlLeniency.Child(action, "argumentList");

        return new ActionDescription
        {
            Name = XmlLeniency.Token(action, "name"),
            Arguments = argumentList is null
                ? []
                : [.. XmlLeniency.Children(argumentList, "argument").Select(ParseArgument)]
        };
    }

    private static ArgumentDescription ParseArgument(XElement argument) => new()
    {
        Name = XmlLeniency.Token(argument, "name"),
        Direction = XmlLeniency.Token(argument, "direction")?.ToLowerInvariant() switch
        {
            "in" => ArgumentDirection.In,
            "out" => ArgumentDirection.Out,
            _ => ArgumentDirection.Unknown
        },
        IsReturnValue = XmlLeniency.Child(argument, "retval") is not null,
        RelatedStateVariable = XmlLeniency.Token(argument, "relatedStateVariable")
    };

    private static StateVariable ParseStateVariable(XElement stateVariable)
    {
        var allowedValueList = XmlLeniency.Child(stateVariable, "allowedValueList");
        var allowedRange = XmlLeniency.Child(stateVariable, "allowedValueRange");

        // sendEvents defaults to "yes" per UDA when the attribute is absent.
        var sendEvents = stateVariable.Attributes().FirstOrDefault(a =>
            string.Equals(a.Name.LocalName, "sendEvents", StringComparison.OrdinalIgnoreCase));

        return new StateVariable
        {
            Name = XmlLeniency.Token(stateVariable, "name"),
            DataType = XmlLeniency.Token(stateVariable, "dataType"),
            DefaultValue = XmlLeniency.Text(stateVariable, "defaultValue"),
            SendsEvents = !string.Equals(sendEvents?.Value.Trim(), "no", StringComparison.OrdinalIgnoreCase),
            AllowedValues = allowedValueList is null
                ? []
                : [.. XmlLeniency.Children(allowedValueList, "allowedValue")
                        .Select(v => v.Value.Trim())
                        .Where(v => v.Length > 0)],
            AllowedRange = allowedRange is null
                ? null
                : new AllowedValueRange
                {
                    Minimum = XmlLeniency.Token(allowedRange, "minimum"),
                    Maximum = XmlLeniency.Token(allowedRange, "maximum"),
                    Step = XmlLeniency.Token(allowedRange, "step")
                }
        };
    }
}
