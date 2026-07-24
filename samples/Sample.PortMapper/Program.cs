using System.Net.NetworkInformation;
using System.Net.Sockets;
using UPnP.Rx.PortMapping;

// Sample.PortMapper — discover the internet gateway, show its state, and
// (optionally) hold an auto-renewing port mapping until Enter is pressed.
//
// Requires a real network with an IGD-capable router; multicast does not work
// in containers.

Console.WriteLine("Searching for an internet gateway (10 s)…");

await using var gateway = await PortMapper.DiscoverGatewayAsync();

if (gateway is null)
{
    var searched = NetworkInterface.GetAllNetworkInterfaces()
        .Where(nic => nic.OperationalStatus is OperationalStatus.Up
            && nic.NetworkInterfaceType is not NetworkInterfaceType.Loopback)
        .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
        .Select(u => u.Address)
        .Where(a => a.AddressFamily is AddressFamily.InterNetwork)
        .Distinct()
        .ToList();

    Console.WriteLine("No internet gateway answered.");
    Console.WriteLine($"Searched from: {(searched.Count == 0 ? "(no usable IPv4 interfaces!)" : string.Join(", ", searched))}");
    Console.WriteLine("""

        Things to check:
          • Is UPnP / IGD enabled on the router? (Often off by default; look under
            "advanced", "NAT forwarding" or "media sharing" in the router UI.)
          • Is a VPN active? Its interface may not reach the LAN — try disconnecting.
          • Running inside Docker/WSL/a devcontainer? Multicast doesn't work there;
            run this sample on the host.
          • On Windows, the "SSDP Discovery" service (SSDPSRV) occupies UDP 1900 and
            keeps clients from seeing responses. Pause it while discovering
            (elevated prompt): net stop SSDPSRV — and resume after: net start SSDPSRV
          • Try Sample.Browser to see whether *any* UPnP device answers at all.
        """);
    return;
}

Console.WriteLine($"Gateway: {gateway.Device.Description.FriendlyName}");
Console.WriteLine($"Service: {gateway.WanConnectionService.Description.ServiceType}");

var status = await gateway.GetStatusInfoAsync();
Console.WriteLine($"WAN: {status.Status} (uptime {status.Uptime}, last error {status.LastError})");
Console.WriteLine($"External IP: {await gateway.GetExternalIPAddressAsync()}");

Console.WriteLine("\nCurrent port mappings:");

await foreach (var mapping in gateway.GetPortMappingsAsync())
{
    Console.WriteLine(
        $"  {mapping.Protocol,-3} {mapping.ExternalPort,5} -> {mapping.InternalClient}:{mapping.InternalPort}" +
        $"  lease {mapping.LeaseDuration}  \"{mapping.Description}\"");
}

if (args.Length > 0 && args[0] == "--map")
{
    var taken = await gateway.GetSpecificPortMappingEntryAsync(18080, Protocol.Tcp);

    if (taken is not null)
    {
        Console.WriteLine($"\nExternal port 18080 is already mapped to {taken.InternalClient} (\"{taken.Description}\").");
        return;
    }

    Console.WriteLine("\nMapping TCP 18080 -> 18080 for 15 minutes (auto-renewing)…");

    await using var lease = await gateway.AddPortMappingAsync(
        externalPort: 18080, internalPort: 18080, Protocol.Tcp,
        description: "UPnP.Rx sample", lease: TimeSpan.FromMinutes(15));

    using var events = lease.Events.Subscribe(e =>
        Console.WriteLine($"  [lease] {e.Kind}{(e.Message is null ? "" : $": {e.Message}")}"));

    Console.WriteLine($"Mapped external port {lease.Mapping.ExternalPort}. Press Enter to release.");
    Console.ReadLine();
}
