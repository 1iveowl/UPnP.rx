namespace UPnP.Rx.Model;

/// <summary>
/// The UPnP architecture version a description document declares
/// (<c>specVersion</c>). Immutable.
/// </summary>
public sealed record SpecVersion
{
    /// <summary>Major architecture version: 1 for UDA 1.x documents, 2 for UDA 2.0.</summary>
    public int Major { get; init; }

    /// <summary>Minor architecture version.</summary>
    public int Minor { get; init; }
}
