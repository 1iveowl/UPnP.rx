namespace UPnP.Rx.PortMapping;

/// <summary>The transport protocol of a port mapping.</summary>
public enum Protocol
{
    /// <summary>TCP.</summary>
    Tcp,

    /// <summary>UDP.</summary>
    Udp
}

/// <summary>Wire-format helpers for <see cref="Protocol"/>.</summary>
public static class ProtocolExtensions
{
    /// <summary>The IGD wire value: <c>TCP</c> or <c>UDP</c>.</summary>
    public static string ToWireString(this Protocol protocol) =>
        protocol == Protocol.Tcp ? "TCP" : "UDP";
}
