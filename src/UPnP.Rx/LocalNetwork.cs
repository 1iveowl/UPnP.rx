using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace UPnP.Rx;

/// <summary>
/// The local interfaces UPnP runs over. Every consumer has to answer "which
/// addresses do I bind?" before it can construct a <see cref="UpnpClient"/>, and
/// the answer is the same everywhere, so it lives here rather than being written
/// out again per application.
/// </summary>
public static class LocalNetwork
{
    /// <summary>
    /// This machine's usable IPv4 addresses: every unicast IPv4 address on an
    /// interface that is up and not loopback, deduplicated.
    /// </summary>
    /// <remarks>
    /// IPv4 only, deliberately: SSDP's multicast group here is 239.255.255.250 and
    /// the installed base this library exists to talk to is IPv4. Loopback is
    /// excluded because no device is reachable across it. The result is a snapshot -
    /// interfaces come and go, so query again rather than caching for the lifetime
    /// of a long-running process.
    /// </remarks>
    /// <returns>The addresses, or an empty array when no interface is usable.</returns>
    public static IPAddress[] IPv4Addresses() =>
        [.. NetworkInterface
            .GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up
                && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
            .Select(unicast => unicast.Address)
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
            .Distinct()];
}
