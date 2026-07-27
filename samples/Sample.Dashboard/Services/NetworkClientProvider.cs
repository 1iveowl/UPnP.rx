using System.Net;

using UPnP.Rx;

namespace Sample.Dashboard.Services;

/// <summary>
/// Owns the one <see cref="UpnpClient"/> both the discovery service and the
/// gateway service share (the library's caller-owned-client pattern).
/// <see cref="Client"/> is null when the host has no usable IPv4 interface.
/// </summary>
public sealed class NetworkClientProvider : IDisposable
{
    public NetworkClientProvider()
    {
        Addresses = LocalNetwork.IPv4Addresses();

        Client = Addresses.Length is 0 ? null : new UpnpClient(new UpnpClientOptions(), Addresses);
    }

    /// <summary>The local IPv4 addresses discovery runs on.</summary>
    public IPAddress[] Addresses { get; }

    /// <summary>The shared client, or null when no interface is usable.</summary>
    public UpnpClient? Client { get; }

    public void Dispose() => Client?.Dispose();
}
