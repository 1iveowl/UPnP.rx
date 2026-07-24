using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;

namespace UPnP.Rx.PortMapping;

/// <summary>
/// The flagship convenience API: discover the internet gateway and map ports in
/// one or two calls.
/// </summary>
public static class PortMapper
{
    private static readonly TimeSpan _defaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Discovers the internet gateway by searching for
    /// <c>InternetGatewayDevice:2</c> and <c>:1</c> on all usable local IPv4
    /// interfaces. The returned gateway owns its discovery client — dispose it
    /// when done.
    /// </summary>
    /// <param name="timeout">How long to wait for a usable gateway; 10 seconds when null.</param>
    /// <param name="ct">Cancels the discovery.</param>
    /// <returns>The gateway, or <see langword="null"/> when none answered in time.</returns>
    public static async Task<InternetGateway?> DiscoverGatewayAsync(
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var client = new UpnpClient(LocalIPv4Addresses());

        try
        {
            var gateway = await DiscoverGatewayAsync(client, timeout, ct).ConfigureAwait(false);

            if (gateway is null)
            {
                client.Dispose();
                return null;
            }

            return gateway.WithOwnedClient(client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Every usable internet gateway on the network, as an observable: searches
    /// for <c>InternetGatewayDevice:2</c> and <c>:1</c>, describes each candidate
    /// concurrently and emits those offering a WAN connection service,
    /// deduplicated by device identity. The scalar
    /// <see cref="DiscoverGatewayAsync(UpnpClient, TimeSpan?, CancellationToken)"/>
    /// is <c>FirstAsync</c> sugar over this stream.
    /// </summary>
    /// <param name="client">The client to discover and control through; the caller keeps ownership.</param>
    /// <remarks>
    /// Temperature: cold — each subscription sends a fresh search. The stream
    /// stays open (gateways keep announcing); compose with <c>FirstAsync</c>,
    /// <c>Take</c> or a timeout as needed. Candidates that cannot be described or
    /// offer no WAN service contribute nothing (per-item failure is data, not
    /// stream death); in-flight description fetches are cancelled on unsubscription.
    /// </remarks>
    public static IObservable<InternetGateway> DiscoverGateways(UpnpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);

        var options = client.Options;

        return client
            .DiscoverDevices(SearchTargets.DeviceType("InternetGatewayDevice", 2))
            .Merge(client.DiscoverDevices(SearchTargets.DeviceType("InternetGatewayDevice", 1)))
            .SelectMany(device => Observable
                // FromAsync ties the token to the subscription: when a consumer
                // unsubscribes (e.g. FirstAsync has a winner), in-flight describes
                // of remaining candidates are cancelled instead of running to
                // their own timeouts.
                .FromAsync(innerCt => TryResolveGatewayAsync(device, options, innerCt))
                .Catch((UpnpException _) =>
                    Observable.Empty<InternetGateway?>()))   // unusable candidate: contributes nothing
            .Where(gateway => gateway is not null)
            .Select(gateway => gateway!)
            // An IGD:2 device answers the IGD:1 search too — one emission per device.
            .Distinct(gateway =>
                gateway.Device.Description.Udn ?? gateway.Device.Description.Location.ToString());
    }

    /// <summary>
    /// Discovers the internet gateway using an existing client (the caller keeps
    /// ownership; dispose the client only after the gateway is no longer used).
    /// <c>FirstAsync</c> sugar over <see cref="DiscoverGateways"/>.
    /// </summary>
    /// <param name="client">The client to discover and control through.</param>
    /// <param name="timeout">How long to wait for a usable gateway; 10 seconds when null.</param>
    /// <param name="ct">Cancels the discovery.</param>
    /// <returns>The gateway, or <see langword="null"/> when none answered in time.</returns>
    public static async Task<InternetGateway?> DiscoverGatewayAsync(
        UpnpClient client,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        using var timeoutCts = new CancellationTokenSource(
            timeout ?? _defaultTimeout, client.Options.TimeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            return await DiscoverGateways(client)
                .FirstAsync()
                .ToTask(linked.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Describes a discovered candidate and wraps it when it has a usable WAN service; null otherwise.</summary>
    private static async Task<InternetGateway?> TryResolveGatewayAsync(
        DiscoveredDevice device,
        UpnpClientOptions options,
        CancellationToken ct)
    {
        var described = await device.GetDescriptionAsync(ct).ConfigureAwait(false);
        var wanService = InternetGateway.ResolveWanService(described);

        return wanService is null
            ? null
            : new InternetGateway(described, wanService, device.LocalEndPoint?.Address, options, ownedClient: null);
    }

    /// <summary>
    /// The one-liner: discover the gateway and create an auto-renewing port
    /// mapping. The returned lease owns the discovery chain — <c>await using</c>
    /// it removes the mapping and releases everything.
    /// </summary>
    /// <exception cref="UpnpException">No gateway answered, or the mapping call failed.</exception>
    public static async Task<PortMappingLease> AddPortMappingAsync(
        ushort externalPort,
        ushort internalPort,
        Protocol protocol,
        string description,
        TimeSpan lease,
        CancellationToken ct = default)
    {
        var gateway = await DiscoverGatewayAsync(timeout: null, ct).ConfigureAwait(false)
            ?? throw new UpnpException("No internet gateway answered the search.");

        try
        {
            var mappingLease = await gateway
                .AddPortMappingAsync(externalPort, internalPort, protocol, description, lease, ct: ct)
                .ConfigureAwait(false);

            return mappingLease.AttachOwnedGateway(gateway);
        }
        catch
        {
            await gateway.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static IPAddress[] LocalIPv4Addresses() =>
        [.. NetworkInterface
            .GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up
                && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
            .Select(unicast => unicast.Address)
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
            .Distinct()];
}
