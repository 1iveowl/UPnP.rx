using System.Net;
using UPnP.Rx.Eventing;
using UPnP.Rx.Model;

namespace UPnP.Rx;

/// <summary>
/// A device whose description document has been fetched and parsed: the full
/// device tree plus stable, controllable <see cref="UpnpService"/> instances for
/// every service in it.
/// </summary>
public sealed class DescribedDevice
{
    internal DescribedDevice(
        DeviceDescription description,
        HttpClient httpClient,
        UpnpClientOptions options,
        EventingContext eventing,
        IPAddress? localAddress,
        CancellationToken lifetime,
        string contentHash = "")
    {
        Description = description;
        ContentHash = contentHash;
        Services =
        [
            // Each service is tagged with the device that declares it, and with the
            // root's CONFIGID - the configuration this description belongs to.
            .. description
                .SelfAndDescendants()
                .SelectMany(device => device.Services.Select(service => new UpnpService(
                    service,
                    new DeviceIdentity(device.Udn, description.ConfigId),
                    httpClient, options, eventing, localAddress, lifetime)))
        ];
    }

    /// <summary>The parsed description document: the root device and its tree.</summary>
    public DeviceDescription Description { get; }

    /// <summary>
    /// What this device's description claims about the UDA version it implements
    /// (<c>&lt;specVersion&gt;</c>). Combine with
    /// <see cref="DiscoveredDevice.VersionClaims"/>, and with each service's
    /// <see cref="UpnpService.VersionClaims"/> once its SCPD is loaded, to see
    /// whether the device agrees with itself.
    /// </summary>
    public UpnpVersionClaims VersionClaims =>
        UpnpVersionClaims.From(
            UpnpVersionSource.DeviceDescription, UpnpVersionClaims.ToVersion(Description.SpecVersion));

    /// <summary>
    /// A hash of the raw description document, for cheap change detection
    /// (the roster's self-healing compares it across re-reads).
    /// </summary>
    internal string ContentHash { get; }

    /// <summary>Every service in the device tree (root and embedded devices), in document order. Stable instances — built once.</summary>
    public IReadOnlyList<UpnpService> Services { get; }

    /// <summary>
    /// Whether any service in the device tree matches
    /// <paramref name="serviceTypeOrId"/> — the full <c>serviceType</c> URN, the
    /// full <c>serviceId</c>, or just the type name (<c>"WANIPConnection"</c>
    /// matches any version).
    /// </summary>
    public bool HasService(string serviceTypeOrId) => Find(serviceTypeOrId) is not null;

    /// <summary>
    /// The first service in the device tree matching
    /// <paramref name="serviceTypeOrId"/> (see <see cref="HasService"/> for the
    /// matching rules; document order, root device first).
    /// </summary>
    /// <exception cref="UpnpException">No service in the tree matches.</exception>
    public UpnpService Service(string serviceTypeOrId) =>
        Find(serviceTypeOrId)
        ?? throw new UpnpException(
            $"The device {Description.FriendlyName ?? Description.Udn} has no service matching '{serviceTypeOrId}'.");

    /// <summary>
    /// The first service matching <paramref name="serviceTypeOrId"/> (same
    /// matching rules as <see cref="Service"/>), or <see langword="null"/> when
    /// the device offers none - the non-throwing lookup for exploratory code.
    /// </summary>
    public UpnpService? TryService(string serviceTypeOrId) => Find(serviceTypeOrId);

    private UpnpService? Find(string serviceTypeOrId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceTypeOrId);

        return Services.FirstOrDefault(service => Matches(service.Description, serviceTypeOrId));
    }

    private static bool Matches(ServiceDescription service, string query) =>
        string.Equals(service.ServiceType, query, StringComparison.OrdinalIgnoreCase)
        || string.Equals(service.ServiceId, query, StringComparison.OrdinalIgnoreCase)
        || string.Equals(TypeName(service.ServiceType), query, StringComparison.OrdinalIgnoreCase);

    /// <summary>The NAME segment of <c>urn:domain:service:NAME:version</c>, or null.</summary>
    private static string? TypeName(string? serviceType)
    {
        if (serviceType is null)
        {
            return null;
        }

        var parts = serviceType.Split(':');

        return parts.Length >= 2 ? parts[^2] : null;
    }
}
