using System.Net;
using System.Net.Sockets;
using Xunit;

namespace UPnP.Rx.Tests;

/// <summary>
/// Environment-independent invariants: whatever interfaces this machine has, the
/// result must be usable as UpnpClient bind addresses.
/// </summary>
public class LocalNetworkTests
{
    [Fact]
    public void IPv4Addresses_AreIPv4_NonLoopback_AndDistinct()
    {
        var addresses = LocalNetwork.IPv4Addresses();

        Assert.All(addresses, a => Assert.Equal(AddressFamily.InterNetwork, a.AddressFamily));
        Assert.All(addresses, a => Assert.False(IPAddress.IsLoopback(a)));
        Assert.Equal(addresses.Length, addresses.Distinct().Count());
    }

    [Fact]
    public void IPv4Addresses_IsASnapshot_NotASharedArray()
    {
        // Callers may sort or mutate the result; a cached array would leak that.
        Assert.NotSame(LocalNetwork.IPv4Addresses(), LocalNetwork.IPv4Addresses());
    }
}
