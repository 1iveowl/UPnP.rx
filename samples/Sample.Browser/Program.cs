using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reactive.Linq;
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
Console.WriteLine("Discovering for 15 seconds…\n");

using var upnp = new UpnpClient(addresses);

using var subscription = upnp
    .DiscoverDevices()                                   // upnp:rootdevice by default
    .SelectMany(async device =>
    {
        try
        {
            return (Discovered: device, Described: (DescribedDevice?)await device.GetDescriptionAsync());
        }
        catch (UpnpException e)
        {
            Console.WriteLine($"! {device.Location}: {e.Message}");
            return (device, null);
        }
    })
    .Where(pair => pair.Described is not null)
    .Subscribe(pair =>
    {
        var root = pair.Described!.Description;
        Console.WriteLine($"● {root.FriendlyName}  [{pair.Discovered.Location}]");
        Print(root, indent: "  ");
        Console.WriteLine();
    });

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
