namespace UPnP.Rx.Model;

/// <summary>
/// A device icon advertised in the description document (<c>iconList/icon</c>).
/// Immutable. Fields the device omitted or botched are left unset (leniency
/// policy: parsers only fail when a document identifies nothing).
/// </summary>
public sealed record IconDescription
{
    /// <summary>The icon's MIME type (<c>mimetype</c>), e.g. <c>image/png</c>.</summary>
    public string? MimeType { get; init; }

    /// <summary>Horizontal size in pixels (<c>width</c>).</summary>
    public int? Width { get; init; }

    /// <summary>Vertical size in pixels (<c>height</c>).</summary>
    public int? Height { get; init; }

    /// <summary>Color depth in bits (<c>depth</c>).</summary>
    public int? Depth { get; init; }

    /// <summary>The icon URL (<c>url</c>), resolved to an absolute URI against the document base.</summary>
    public Uri? Url { get; init; }
}
