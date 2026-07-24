namespace UPnP.Rx.Model;

/// <summary>
/// A device from a device description document (DDD, UDA 2.0 clause 2): the root
/// device or an embedded device from its <c>deviceList</c>. Immutable. Fields the
/// device omitted or botched are left unset (leniency policy: parsing only fails
/// when a document identifies nothing).
/// </summary>
public sealed record DeviceDescription
{
    /// <summary>The URL the description document was fetched from; identity for caching.</summary>
    public required Uri Location { get; init; }

    /// <summary>
    /// The absolute base every relative URL in the document was resolved against:
    /// <see cref="Location"/> per UDA 2.0, or the document's <c>URLBase</c> when a
    /// UDA 1.0-era device supplies one (leniency: a device that ships
    /// <c>URLBase</c> expects it to be honored).
    /// </summary>
    public required Uri BaseUrl { get; init; }

    /// <summary>The architecture version the document declares (<c>specVersion</c>).</summary>
    public SpecVersion? SpecVersion { get; init; }

    /// <summary>The description's configuration number (<c>configId</c> attribute, UDA 2.0), if declared.</summary>
    public int? ConfigId { get; init; }

    /// <summary>The device type URN (<c>deviceType</c>), e.g. <c>urn:schemas-upnp-org:device:InternetGatewayDevice:2</c>.</summary>
    public string? DeviceType { get; init; }

    /// <summary>Short user-facing name (<c>friendlyName</c>).</summary>
    public string? FriendlyName { get; init; }

    /// <summary>The unique device name (<c>UDN</c>), e.g. <c>uuid:…</c>; stable across boots.</summary>
    public string? Udn { get; init; }

    /// <summary>Manufacturer name (<c>manufacturer</c>).</summary>
    public string? Manufacturer { get; init; }

    /// <summary>Manufacturer web site (<c>manufacturerURL</c>).</summary>
    public string? ManufacturerUrl { get; init; }

    /// <summary>Long user-facing description (<c>modelDescription</c>).</summary>
    public string? ModelDescription { get; init; }

    /// <summary>Model name (<c>modelName</c>).</summary>
    public string? ModelName { get; init; }

    /// <summary>Model number (<c>modelNumber</c>).</summary>
    public string? ModelNumber { get; init; }

    /// <summary>Model web site (<c>modelURL</c>).</summary>
    public string? ModelUrl { get; init; }

    /// <summary>Serial number (<c>serialNumber</c>).</summary>
    public string? SerialNumber { get; init; }

    /// <summary>Universal product code (<c>UPC</c>).</summary>
    public string? Upc { get; init; }

    /// <summary>The device's web page (<c>presentationURL</c>), resolved to absolute.</summary>
    public Uri? PresentationUrl { get; init; }

    /// <summary>The device's icons (<c>iconList</c>); empty when none are advertised.</summary>
    public IReadOnlyList<IconDescription> Icons { get; init; } = [];

    /// <summary>The device's services (<c>serviceList</c>); empty when none are advertised.</summary>
    public IReadOnlyList<ServiceDescription> Services { get; init; } = [];

    /// <summary>Embedded devices (<c>deviceList</c>); empty when there are none.</summary>
    public IReadOnlyList<DeviceDescription> EmbeddedDevices { get; init; } = [];

    /// <summary>
    /// This device followed by all embedded devices, depth-first — the full device
    /// tree flattened, e.g. for locating a service anywhere in an
    /// InternetGatewayDevice hierarchy.
    /// </summary>
    public IEnumerable<DeviceDescription> SelfAndDescendants()
    {
        yield return this;

        foreach (var embedded in EmbeddedDevices)
        {
            foreach (var descendant in embedded.SelfAndDescendants())
            {
                yield return descendant;
            }
        }
    }
}
