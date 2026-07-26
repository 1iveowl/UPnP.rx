using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using UPnP.Rx.Model;

namespace UPnP.Rx.PortMapping;

/// <summary>
/// An internet gateway's port-mapping surface: the WAN connection service
/// (<c>WANIPConnection:2/:1</c> or <c>WANPPPConnection:1</c>) of a discovered
/// <c>InternetGatewayDevice</c>, wrapped in a typed API.
/// </summary>
public sealed class InternetGateway : IInternetGateway
{
    // Best first: prefer IP over PPP, higher version over lower. WANPPPConnection:2
    // is real (see the Orange Livebox fixture) even though the plan only listed :1.
    private static readonly string[] _servicePriority =
    [
        "urn:schemas-upnp-org:service:WANIPConnection:2",
        "urn:schemas-upnp-org:service:WANIPConnection:1",
        "urn:schemas-upnp-org:service:WANPPPConnection:2",
        "urn:schemas-upnp-org:service:WANPPPConnection:1"
    ];

    private readonly UpnpClient? _ownedClient;
    private readonly UpnpClientOptions _options;

    internal InternetGateway(
        DescribedDevice device,
        UpnpService wanService,
        IPAddress? localAddress,
        UpnpClientOptions options,
        UpnpClient? ownedClient)
    {
        Device = device;
        WanConnectionService = wanService;
        LocalAddress = localAddress;
        _options = options;
        _ownedClient = ownedClient;
    }

    /// <summary>The gateway's described device tree.</summary>
    public DescribedDevice Device { get; }

    /// <summary>The WAN connection service in use (best available: WANIPConnection:2, :1, then WANPPPConnection:1).</summary>
    public UpnpService WanConnectionService { get; }

    /// <summary>
    /// This machine's address on the network shared with the gateway (from the
    /// discovery exchange, when it revealed one); the default internal client
    /// for new mappings.
    /// </summary>
    public IPAddress? LocalAddress { get; }

    /// <summary>
    /// The envelope answers when the SSDP socket was interface-bound; otherwise
    /// the routing table says which of our addresses faces the gateway.
    /// </summary>
    private IPAddress? DefaultInternalClient() =>
        LocalRoute.IsUsable(LocalAddress) ? LocalAddress
        : WanConnectionService.Description.ControlUrl is { } control ? LocalRoute.Resolve(control)
        : null;

    /// <summary>Resolves the gateway's WAN connection service from a described device, best version first.</summary>
    internal static UpnpService? ResolveWanService(DescribedDevice device) =>
        _servicePriority
            .Where(device.HasService)
            .Select(device.Service)
            .FirstOrDefault();

    /// <summary>The gateway's external (WAN) IP address.</summary>
    /// <exception cref="UpnpActionException">The gateway answered with a UPnP error.</exception>
    /// <exception cref="UpnpException">The call failed or the answer was not an IP address.</exception>
    public async Task<IPAddress> GetExternalIPAddressAsync(CancellationToken ct = default)
    {
        var result = await WanConnectionService
            .InvokeAsync("GetExternalIPAddress", ct: ct)
            .ConfigureAwait(false);

        return IPAddress.TryParse(result["NewExternalIPAddress"], out var address)
            ? address
            : throw new UpnpException(
                $"The gateway returned an unparsable external address: '{result["NewExternalIPAddress"]}'.");
    }

    /// <summary>
    /// Creates a port mapping and returns an auto-renewing lease for it
    /// (decision 3): a finite <paramref name="lease"/> is renewed at half-life on
    /// the options' <see cref="UpnpClientOptions.TimeProvider"/>; renewal outcomes
    /// surface on <see cref="PortMappingLease.Events"/>. Dispose the lease with
    /// <c>await using</c> to remove the mapping from the gateway.
    /// </summary>
    /// <param name="externalPort">The WAN-side port.</param>
    /// <param name="internalPort">The LAN-side port.</param>
    /// <param name="protocol">The transport protocol.</param>
    /// <param name="description">The description stored on the gateway.</param>
    /// <param name="lease">
    /// The lease duration; <see cref="TimeSpan.Zero"/> for an indefinite mapping —
    /// documented opt-out of both auto-renewal and expiry-on-abrupt-dispose.
    /// </param>
    /// <param name="internalClient">The LAN-side host; defaults to <see cref="LocalAddress"/>, or to the interface that routes toward the gateway when discovery did not reveal one.</param>
    /// <param name="ct">Cancels the initial mapping call.</param>
    /// <exception cref="UpnpActionException">The gateway refused (e.g. 718 ConflictInMappingEntry).</exception>
    /// <exception cref="UpnpException">No internal client address is known, or the call failed.</exception>
    public Task<PortMappingLease> AddPortMappingAsync(
        ushort externalPort,
        ushort internalPort,
        Protocol protocol,
        string description,
        TimeSpan lease,
        IPAddress? internalClient = null,
        CancellationToken ct = default) =>
        AddAsync(externalPort, internalPort, protocol, description, lease, internalClient, useAnyPort: false, ct);

    /// <summary>
    /// IGD:2 only — asks the gateway for <paramref name="externalPort"/> or, when
    /// taken, <em>any</em> free port (<c>AddAnyPortMapping</c>). The granted port
    /// is in the returned lease's <see cref="PortMappingLease.Mapping"/>.
    /// </summary>
    /// <exception cref="UpnpException">The gateway's service is not WANIPConnection:2.</exception>
    /// <inheritdoc cref="AddPortMappingAsync(ushort, ushort, Protocol, string, TimeSpan, IPAddress?, CancellationToken)"/>
    public Task<PortMappingLease> AddAnyPortMappingAsync(
        ushort externalPort,
        ushort internalPort,
        Protocol protocol,
        string description,
        TimeSpan lease,
        IPAddress? internalClient = null,
        CancellationToken ct = default)
    {
        return !string.Equals(
                WanConnectionService.Description.ServiceType,
                "urn:schemas-upnp-org:service:WANIPConnection:2",
                StringComparison.OrdinalIgnoreCase)
            ? throw new UpnpException(
                $"AddAnyPortMapping requires WANIPConnection:2; this gateway offers {WanConnectionService.Description.ServiceType}.")
            : AddAsync(externalPort, internalPort, protocol, description, lease, internalClient, useAnyPort: true, ct);
    }

    /// <summary>The WAN connection state (<c>GetStatusInfo</c>): status, last error, uptime.</summary>
    /// <exception cref="UpnpActionException">The gateway answered with a UPnP error.</exception>
    public async Task<ConnectionStatusInfo> GetStatusInfoAsync(CancellationToken ct = default)
    {
        var result = await WanConnectionService
            .InvokeAsync("GetStatusInfo", ct: ct)
            .ConfigureAwait(false);

        return new ConnectionStatusInfo
        {
            Status = result["NewConnectionStatus"],
            LastError = result["NewLastConnectionError"],
            Uptime = uint.TryParse(result["NewUptime"], out var seconds)
                ? TimeSpan.FromSeconds(seconds)
                : TimeSpan.Zero
        };
    }

    /// <summary>
    /// The mapping for one external port + protocol
    /// (<c>GetSpecificPortMappingEntry</c>), or <see langword="null"/> when the
    /// gateway reports none (UPnP error 714 NoSuchEntryInArray).
    /// </summary>
    /// <exception cref="UpnpActionException">The gateway answered with a UPnP error other than 714.</exception>
    public async Task<PortMappingEntry?> GetSpecificPortMappingEntryAsync(
        ushort externalPort,
        Protocol protocol,
        string remoteHost = "",
        CancellationToken ct = default)
    {
        ActionResult entry;

        try
        {
            entry = await WanConnectionService.InvokeAsync("GetSpecificPortMappingEntry",
                new Dictionary<string, string>
                {
                    ["NewRemoteHost"] = remoteHost,
                    ["NewExternalPort"] = externalPort.ToString(),
                    ["NewProtocol"] = protocol.ToWireString()
                }, ct).ConfigureAwait(false);
        }
        catch (UpnpActionException e) when (e.Error.Code is 714)
        {
            return null;
        }

        return new PortMappingEntry
        {
            RemoteHost = remoteHost,
            ExternalPort = externalPort,
            Protocol = protocol,
            InternalPort = ushort.TryParse(entry["NewInternalPort"], out var internalPort) ? internalPort : (ushort)0,
            InternalClient = entry["NewInternalClient"],
            Enabled = entry["NewEnabled"] is "1" or "true",
            Description = entry["NewPortMappingDescription"],
            LeaseDuration = uint.TryParse(entry["NewLeaseDuration"], out var seconds)
                ? TimeSpan.FromSeconds(seconds)
                : TimeSpan.Zero
        };
    }

    /// <summary>Removes a port mapping from the gateway.</summary>
    /// <exception cref="UpnpActionException">The gateway answered with a UPnP error (e.g. 714 NoSuchEntryInArray).</exception>
    public async Task DeletePortMappingAsync(
        ushort externalPort,
        Protocol protocol,
        CancellationToken ct = default) =>
            await WanConnectionService.InvokeAsync("DeletePortMapping", new Dictionary<string, string>
            {
                ["NewRemoteHost"] = string.Empty,
                ["NewExternalPort"] = externalPort.ToString(),
                ["NewProtocol"] = protocol.ToWireString()
            }, ct).ConfigureAwait(false);

    /// <summary>
    /// Enumerates the gateway's port mappings via
    /// <c>GetGenericPortMappingEntry</c>. Enumeration ends at the gateway's first
    /// fault (713 SpecifiedArrayIndexInvalid per spec; devices vary — leniency),
    /// or at 65 535 entries as a guard against broken gateways that answer every
    /// index forever.
    /// </summary>
    public async IAsyncEnumerable<PortMappingEntry> GetPortMappingsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        for (var index = 0; index <= ushort.MaxValue; index++)
        {
            ActionResult entry;

            try
            {
                entry = await WanConnectionService.InvokeAsync("GetGenericPortMappingEntry",
                    new Dictionary<string, string> { ["NewPortMappingIndex"] = index.ToString() }, ct)
                    .ConfigureAwait(false);
            }
            catch (UpnpActionException e)
            {
                _options.Logger.PortMappingEnumerationEnded(index, e.Error.Code);
                yield break;
            }

            yield return new PortMappingEntry
            {
                RemoteHost = entry["NewRemoteHost"],
                ExternalPort = ushort.TryParse(entry["NewExternalPort"], out var external) ? external : (ushort)0,
                InternalPort = ushort.TryParse(entry["NewInternalPort"], out var internalPort) ? internalPort : (ushort)0,
                Protocol = string.Equals(entry["NewProtocol"], "UDP", StringComparison.OrdinalIgnoreCase)
                    ? Protocol.Udp
                    : Protocol.Tcp,
                InternalClient = entry["NewInternalClient"],
                Enabled = entry["NewEnabled"] is "1" or "true",
                Description = entry["NewPortMappingDescription"],
                LeaseDuration = uint.TryParse(entry["NewLeaseDuration"], out var seconds)
                    ? TimeSpan.FromSeconds(seconds)
                    : TimeSpan.Zero
            };
        }
    }

    /// <summary>Renews (re-adds) an existing mapping — the standard IGD lease-refresh technique.</summary>
    internal Task<ActionResult> RenewAsync(PortMappingEntry mapping, CancellationToken ct) =>
        InvokeAddPortMappingAsync(mapping, ct);

    /// <summary>A copy of this gateway that owns (and disposes) the given discovery client.</summary>
    internal InternetGateway WithOwnedClient(UpnpClient client) =>
        new(Device, WanConnectionService, LocalAddress, _options, client);

    // IInternetGateway exposes the interface types; the class keeps the concrete
    // ones so direct users lose nothing.
    IUpnpService IInternetGateway.WanConnectionService => WanConnectionService;

    async Task<IPortMappingLease> IInternetGateway.AddPortMappingAsync(
        ushort externalPort, ushort internalPort, Protocol protocol, string description,
        TimeSpan lease, IPAddress? internalClient, CancellationToken ct) =>
            await AddPortMappingAsync(externalPort, internalPort, protocol, description, lease, internalClient, ct)
                .ConfigureAwait(false);

    async Task<IPortMappingLease> IInternetGateway.AddAnyPortMappingAsync(
        ushort externalPort, ushort internalPort, Protocol protocol, string description,
        TimeSpan lease, IPAddress? internalClient, CancellationToken ct) =>
            await AddAnyPortMappingAsync(externalPort, internalPort, protocol, description, lease, internalClient, ct)
                .ConfigureAwait(false);

    /// <summary>Releases the discovery client when this gateway owns one (created via <see cref="PortMapper"/>).</summary>
    public void Dispose() => _ownedClient?.Dispose();

    /// <summary>Releases the discovery client when this gateway owns one.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_ownedClient is not null)
        {
            await _ownedClient.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<PortMappingLease> AddAsync(
        ushort externalPort,
        ushort internalPort,
        Protocol protocol,
        string description,
        TimeSpan lease,
        IPAddress? internalClient,
        bool useAnyPort,
        CancellationToken ct)
    {
        var client = internalClient ?? DefaultInternalClient()
            ?? throw new UpnpException(
                "No internal client address: pass internalClient explicitly (the discovery exchange did not reveal a local address, and no route toward the gateway was found).");

        var mapping = new PortMappingEntry
        {
            RemoteHost = string.Empty,
            ExternalPort = externalPort,
            InternalPort = internalPort,
            Protocol = protocol,
            InternalClient = client.ToString(),
            Enabled = true,
            Description = description,
            LeaseDuration = lease
        };

        if (useAnyPort)
        {
            var result = await WanConnectionService
                .InvokeAsync("AddAnyPortMapping", AddArguments(mapping), ct)
                .ConfigureAwait(false);

            if (ushort.TryParse(result["NewReservedPort"], out var granted))
            {
                mapping = mapping with { ExternalPort = granted };
            }
        }
        else
        {
            await InvokeAddPortMappingAsync(mapping, ct).ConfigureAwait(false);
        }

        return new PortMappingLease(this, mapping, _options);
    }

    private Task<ActionResult> InvokeAddPortMappingAsync(PortMappingEntry mapping, CancellationToken ct) =>
        WanConnectionService.InvokeAsync("AddPortMapping", AddArguments(mapping), ct);

    /// <summary>In-arguments in SCPD declaration order (strict in what we send).</summary>
    private static Dictionary<string, string> AddArguments(PortMappingEntry mapping) => new()
    {
        ["NewRemoteHost"] = mapping.RemoteHost ?? string.Empty,
        ["NewExternalPort"] = mapping.ExternalPort.ToString(),
        ["NewProtocol"] = mapping.Protocol.ToWireString(),
        ["NewInternalPort"] = mapping.InternalPort.ToString(),
        ["NewInternalClient"] = mapping.InternalClient ?? string.Empty,
        ["NewEnabled"] = mapping.Enabled ? "1" : "0",
        ["NewPortMappingDescription"] = mapping.Description ?? string.Empty,
        ["NewLeaseDuration"] = ((uint)mapping.LeaseDuration.TotalSeconds).ToString()
    };
}
