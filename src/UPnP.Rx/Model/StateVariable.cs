namespace UPnP.Rx.Model;

/// <summary>A state variable from a service's SCPD (<c>serviceStateTable/stateVariable</c>). Immutable.</summary>
public sealed record StateVariable
{
    /// <summary>The variable name (<c>name</c>).</summary>
    public string? Name { get; init; }

    /// <summary>The UPnP data type (<c>dataType</c>), e.g. <c>ui4</c>, <c>string</c>, <c>boolean</c>.</summary>
    public string? DataType { get; init; }

    /// <summary>The default value (<c>defaultValue</c>), if declared.</summary>
    public string? DefaultValue { get; init; }

    /// <summary>
    /// Whether changes to the variable are evented (<c>sendEvents</c> attribute);
    /// defaults to <see langword="true"/> per UDA when the attribute is absent.
    /// </summary>
    public bool SendsEvents { get; init; } = true;

    /// <summary>The allowed values (<c>allowedValueList</c>); empty when unrestricted.</summary>
    public IReadOnlyList<string> AllowedValues { get; init; } = [];

    /// <summary>The allowed numeric range (<c>allowedValueRange</c>), if declared.</summary>
    public AllowedValueRange? AllowedRange { get; init; }
}

/// <summary>
/// The allowed numeric range of a state variable (<c>allowedValueRange</c>).
/// Values are kept as the document's strings, since their interpretation depends
/// on the variable's UPnP data type. Immutable.
/// </summary>
public sealed record AllowedValueRange
{
    /// <summary>Inclusive lower bound (<c>minimum</c>).</summary>
    public string? Minimum { get; init; }

    /// <summary>Inclusive upper bound (<c>maximum</c>).</summary>
    public string? Maximum { get; init; }

    /// <summary>Value granularity (<c>step</c>), if declared.</summary>
    public string? Step { get; init; }
}
