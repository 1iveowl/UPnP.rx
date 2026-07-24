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
        Eventing.EventingContext eventing,
        System.Net.IPAddress? localAddress,
        CancellationToken lifetime)
    {
        Description = description;
        Services =
        [
            .. description
                .SelfAndDescendants()
                .SelectMany(device => device.Services)
                .Select(service => new UpnpService(service, httpClient, options, eventing, localAddress, lifetime))
        ];
    }

    /// <summary>The parsed description document: the root device and its tree.</summary>
    public DeviceDescription Description { get; }

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
