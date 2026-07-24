using System.Net;
using System.Net.Sockets;

namespace UPnP.Rx;

/// <summary>
/// "Which of our addresses faces this device" - needed wherever we hand a
/// device an address of ours (event callback URLs, port-mapping internal
/// clients). The discovery envelope answers it only when the SSDP socket was
/// bound to a concrete interface; on macOS/Linux that socket is wildcard-bound,
/// so the envelope reports 0.0.0.0 and the routing table must answer instead.
/// </summary>
internal static class LocalRoute
{
    /// <summary>Whether an envelope-reported address can be handed to a device.</summary>
    internal static bool IsUsable(IPAddress? address) =>
        address is not null && !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any);

    /// <summary>
    /// The local address that routes toward the device: a connectionless
    /// connect makes the OS pick the outgoing interface, without sending
    /// anything. Null when the OS has no route.
    /// </summary>
    internal static IPAddress? Resolve(Uri deviceUrl)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(deviceUrl.Host, deviceUrl.Port);
            return ((IPEndPoint)socket.LocalEndPoint!).Address;
        }
        catch (SocketException)
        {
            return null;
        }
    }
}
