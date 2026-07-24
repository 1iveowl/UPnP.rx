using System.Net;

namespace UPnP.Rx.PortMapping;

/// <summary>
/// An internet gateway's port-mapping surface. Implemented by
/// <see cref="InternetGateway"/>; exists so consumers can fake a gateway in
/// their own tests without replaying SSDP/DDD/SOAP exchanges.
/// </summary>
public interface IInternetGateway : IAsyncDisposable, IDisposable
{
    /// <summary>The gateway's described device tree.</summary>
    DescribedDevice Device { get; }

    /// <summary>The WAN connection service in use.</summary>
    IUpnpService WanConnectionService { get; }

    /// <summary>This machine's address on the network shared with the gateway, when known.</summary>
    IPAddress? LocalAddress { get; }

    /// <summary>The gateway's external (WAN) IP address.</summary>
    Task<IPAddress> GetExternalIPAddressAsync(CancellationToken ct = default);

    /// <summary>The WAN connection state (<c>GetStatusInfo</c>).</summary>
    Task<ConnectionStatusInfo> GetStatusInfoAsync(CancellationToken ct = default);

    /// <summary>Creates a port mapping and returns its auto-renewing lease.</summary>
    Task<IPortMappingLease> AddPortMappingAsync(
        ushort externalPort,
        ushort internalPort,
        Protocol protocol,
        string description,
        TimeSpan lease,
        IPAddress? internalClient = null,
        CancellationToken ct = default);

    /// <summary>IGD:2 only — maps the requested port or any free one; see the granted lease's mapping.</summary>
    Task<IPortMappingLease> AddAnyPortMappingAsync(
        ushort externalPort,
        ushort internalPort,
        Protocol protocol,
        string description,
        TimeSpan lease,
        IPAddress? internalClient = null,
        CancellationToken ct = default);

    /// <summary>Removes a port mapping from the gateway.</summary>
    Task DeletePortMappingAsync(ushort externalPort, Protocol protocol, CancellationToken ct = default);

    /// <summary>The mapping for one external port + protocol, or <see langword="null"/> when none exists.</summary>
    Task<PortMappingEntry?> GetSpecificPortMappingEntryAsync(
        ushort externalPort,
        Protocol protocol,
        string remoteHost = "",
        CancellationToken ct = default);

    /// <summary>Enumerates the gateway's port mappings.</summary>
    IAsyncEnumerable<PortMappingEntry> GetPortMappingsAsync(CancellationToken ct = default);
}
