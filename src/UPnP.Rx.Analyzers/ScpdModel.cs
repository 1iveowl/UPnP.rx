using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace UPnP.Rx.Analyzers;

/// <summary>
/// The pieces of an SCPD document the generator needs, as small equatable values.
/// </summary>
/// <remarks>
/// Equatable and free of Roslyn types on purpose: everything that enters an
/// <c>IIncrementalGenerator</c> pipeline is compared for equality to decide whether
/// downstream steps can be skipped, and a model carrying <c>ISymbol</c>, <c>Compilation</c>
/// or syntax nodes both defeats that comparison and keeps whole compilations alive.
/// </remarks>
internal sealed record ScpdDocument(string ServiceName, IReadOnlyList<ScpdAction> Actions)
{
    /// <inheritdoc />
    public bool Equals(ScpdDocument? other) =>
        other is not null
        && ServiceName == other.ServiceName
        && Actions.SequenceEqual(other.Actions);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = ServiceName.GetHashCode();

        foreach (var action in Actions)
        {
            hash = (hash * 397) ^ action.GetHashCode();
        }

        return hash;
    }
}

/// <summary>One action, with its arguments in the order the document declares them.</summary>
internal sealed record ScpdAction(string Name, IReadOnlyList<ScpdArgument> Arguments)
{
    /// <summary>The in-arguments, in SCPD order - which is the order the wire requires.</summary>
    public IEnumerable<ScpdArgument> In => Arguments.Where(a => !a.IsOut);

    /// <summary>The out-arguments, in SCPD order.</summary>
    public IEnumerable<ScpdArgument> Out => Arguments.Where(a => a.IsOut);

    /// <inheritdoc />
    public bool Equals(ScpdAction? other) =>
        other is not null && Name == other.Name && Arguments.SequenceEqual(other.Arguments);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = Name.GetHashCode();

        foreach (var argument in Arguments)
        {
            hash = (hash * 397) ^ argument.GetHashCode();
        }

        return hash;
    }
}

/// <summary>
/// One argument, resolved against its related state variable so the generator does not
/// have to carry the variable table around.
/// </summary>
/// <param name="Name">The wire name, e.g. <c>NewExternalPort</c>.</param>
/// <param name="IsOut">Whether it is an out-argument. Unknown directions count as in, matching the runtime marshaller's leniency.</param>
/// <param name="DataType">The SCPD data type of the related state variable, lower-cased.</param>
/// <param name="Minimum">The declared <c>allowedValueRange</c> minimum, when there is one.</param>
/// <param name="Maximum">The declared <c>allowedValueRange</c> maximum, when there is one.</param>
/// <param name="AllowedValues">The declared <c>allowedValueList</c>, empty when unconstrained.</param>
internal sealed record ScpdArgument(
    string Name,
    bool IsOut,
    string DataType,
    decimal? Minimum,
    decimal? Maximum,
    IReadOnlyList<string> AllowedValues)
{
    /// <inheritdoc />
    public bool Equals(ScpdArgument? other) =>
        other is not null
        && Name == other.Name
        && IsOut == other.IsOut
        && DataType == other.DataType
        && Minimum == other.Minimum
        && Maximum == other.Maximum
        && AllowedValues.SequenceEqual(other.AllowedValues);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = Name.GetHashCode();
        hash = (hash * 397) ^ IsOut.GetHashCode();
        hash = (hash * 397) ^ DataType.GetHashCode();
        hash = (hash * 397) ^ (Minimum?.GetHashCode() ?? 0);
        hash = (hash * 397) ^ (Maximum?.GetHashCode() ?? 0);

        foreach (var value in AllowedValues)
        {
            hash = (hash * 397) ^ value.GetHashCode();
        }

        return hash;
    }
}

/// <summary>Reads an SCPD document into <see cref="ScpdDocument"/>. Pure and total.</summary>
internal static class ScpdReader
{
    /// <summary>
    /// Parses the document, or returns null when it identifies no actions at all.
    /// </summary>
    /// <remarks>
    /// Namespace- and case-tolerant by local name, matching the library's own parsers: the
    /// documents in the wild are not reliably namespaced, and a generator that refused them
    /// would be stricter about a checked-in file than the runtime is about a live device.
    /// </remarks>
    public static ScpdDocument? Read(string xml, string serviceName)
    {
        XDocument document;

        try
        {
            document = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }

        if (document.Root is null)
        {
            return null;
        }

        var variables = Elements(document.Root, "stateVariable")
            .Select(v => new
            {
                Name = Value(v, "name"),
                DataType = (Value(v, "dataType") ?? string.Empty).ToLowerInvariant(),
                Minimum = Decimal(Descend(v, "allowedValueRange", "minimum")),
                Maximum = Decimal(Descend(v, "allowedValueRange", "maximum")),
                Allowed = Descendants(v, "allowedValue").Select(a => a.Value.Trim()).ToList()
            })
            .Where(v => v.Name is not null)
            .GroupBy(v => v.Name!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var actions = new List<ScpdAction>();

        foreach (var action in Elements(document.Root, "action"))
        {
            if (Value(action, "name") is not { Length: > 0 } name)
            {
                continue;
            }

            var arguments = new List<ScpdArgument>();

            foreach (var argument in Descendants(action, "argument"))
            {
                if (Value(argument, "name") is not { Length: > 0 } argumentName)
                {
                    continue;
                }

                var related = Value(argument, "relatedStateVariable");
                var variable = related is not null && variables.TryGetValue(related, out var found) ? found : null;

                arguments.Add(new ScpdArgument(
                    argumentName,
                    // Unknown direction counts as "in", matching ValidateAndOrderArguments.
                    IsOut: string.Equals(Value(argument, "direction"), "out", StringComparison.OrdinalIgnoreCase),
                    DataType: variable?.DataType ?? string.Empty,
                    Minimum: variable?.Minimum,
                    Maximum: variable?.Maximum,
                    AllowedValues: variable?.Allowed ?? []));
            }

            actions.Add(new ScpdAction(name, arguments));
        }

        return actions.Count > 0 ? new ScpdDocument(serviceName, actions) : null;
    }

    private static IEnumerable<XElement> Elements(XElement root, string localName) =>
        root.Descendants().Where(e => string.Equals(e.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<XElement> Descendants(XElement element, string localName) =>
        element.Descendants().Where(e => string.Equals(e.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase));

    private static string? Value(XElement element, string localName) =>
        Descendants(element, localName).FirstOrDefault()?.Value.Trim();

    private static string? Descend(XElement element, string outer, string inner) =>
        Descendants(element, outer).FirstOrDefault() is { } found ? Value(found, inner) : null;

    private static decimal? Decimal(string? text) =>
        decimal.TryParse(text, System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
}
