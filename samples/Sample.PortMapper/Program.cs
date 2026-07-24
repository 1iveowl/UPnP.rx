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
    Console.WriteLine("No internet gateway answered. Is UPnP/IGD enabled on the router?");
    return;
}

Console.WriteLine($"Gateway: {gateway.Device.Description.FriendlyName}");
Console.WriteLine($"Service: {gateway.WanConnectionService.Description.ServiceType}");
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
    Console.WriteLine("\nMapping TCP 18080 -> 18080 for 15 minutes (auto-renewing)…");

    await using var lease = await gateway.AddPortMappingAsync(
        externalPort: 18080, internalPort: 18080, Protocol.Tcp,
        description: "UPnP.Rx sample", lease: TimeSpan.FromMinutes(15));

    using var events = lease.Events.Subscribe(e =>
        Console.WriteLine($"  [lease] {e.Kind}{(e.Message is null ? "" : $": {e.Message}")}"));

    Console.WriteLine($"Mapped external port {lease.Mapping.ExternalPort}. Press Enter to release.");
    Console.ReadLine();
}
