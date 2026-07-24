using System.Net.NetworkInformation;
using System.Net.Sockets;
using UPnP.Rx;
using UPnP.Rx.Model;

// Sample.Browser — discover every UPnP device on the network and dump its
// device tree and services. Requires a real network; multicast does not work
// in containers.

var addresses = NetworkInterface.GetAllNetworkInterfaces()
    .Where(nic => nic.OperationalStatus is OperationalStatus.Up
        && nic.NetworkInterfaceType is not NetworkInterfaceType.Loopback)
    .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
    .Select(u => u.Address)
    .Where(a => a.AddressFamily is AddressFamily.InterNetwork)
    .Distinct()
    .ToArray();

if (addresses.Length is 0)
{
    Console.WriteLine("No usable IPv4 interfaces found.");
    return;
}

Console.WriteLine($"Browsing from: {string.Join(", ", addresses.Select(a => a.ToString()))}");
Console.WriteLine("Discovering (press Enter to stop)…\n");

using var upnp = new UpnpClient(addresses);

var found = 0;

// One line from discovery to described device trees; failures are skipped
// (pass an ILogger via UpnpClientOptions to see them).
using var subscription = upnp
    .DiscoverDescribedDevices()
    .Subscribe(device =>
    {
        Interlocked.Increment(ref found);
        Console.WriteLine($"● {device.Description.FriendlyName}  [{device.Description.Location}]");
        Print(device.Description, indent: "  ");
        Console.WriteLine();
    });

await Task.Delay(TimeSpan.FromSeconds(15), TimeProvider.System);

if (Volatile.Read(ref found) is 0)
{
    Console.WriteLine("""
        Nothing answered in 15 seconds. Things to check:
          • Running inside Docker/WSL/a devcontainer? Multicast doesn't work there;
            run this sample on the host.
          • Is a VPN active? Try disconnecting.
          • On Windows, the "SSDP Discovery" service (SSDPSRV) occupies UDP 1900 and
            keeps clients from seeing responses. Pause it while discovering
            (elevated prompt): net stop SSDPSRV — and resume after: net start SSDPSRV
          • Some networks block SSDP (AP isolation, IGMP snooping) — try another
            network or a wired connection.
        Still listening — devices announce themselves periodically…
        """);
}

Console.ReadLine();

static void Print(DeviceDescription device, string indent)
{
    Console.WriteLine($"{indent}{device.DeviceType}  ({device.Manufacturer} {device.ModelName})");

    foreach (var service in device.Services)
    {
        Console.WriteLine($"{indent}  ⚙ {service.ServiceType}");
    }

    foreach (var embedded in device.EmbeddedDevices)
    {
        Print(embedded, indent + "  ");
    }
}
