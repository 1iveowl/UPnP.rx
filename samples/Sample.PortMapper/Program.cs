using UPnP.Rx.PortMapping;
using UPnP.Rx;

// Sample.PortMapper - discover the internet gateway, show its state, and
// (optionally) hold an auto-renewing port mapping until Enter is pressed.
//
// Requires a real network with an IGD-capable router; multicast does not work
// in containers. Colors via Console.ForegroundColor (portable); ASCII-safe glyphs.

Write(ConsoleColor.Cyan, "UPnP.Rx");
Console.WriteLine(" port mapper");
WriteLine(ConsoleColor.DarkGray, "Searching for an internet gateway (10 s)...");

await using var gateway = await PortMapper.DiscoverGatewayAsync();

if (gateway is null)
{
    var searched = LocalNetwork.IPv4Addresses();

    WriteLine(ConsoleColor.Red, "No internet gateway answered.");
    Write(ConsoleColor.DarkGray, "Searched from: ");
    Console.WriteLine(searched.Length is 0 ? "(no usable IPv4 interfaces!)" : string.Join(", ", searched));
    WriteLine(ConsoleColor.Yellow, """

        Things to check:
          - Is UPnP / IGD enabled on the router? It is usually off by default. On
            UniFi: Settings -> Internet -> your WAN -> UPnP (older controllers:
            Settings -> Services -> UPnP); other routers hide it under NAT,
            port-forwarding or "media sharing" settings. Note: the UPnP endpoint is
            LAN-side only, but unauthenticated - any LAN device can open WAN ports.
          - Is a VPN active? Its interface may not reach the LAN - try disconnecting.
          - Running inside Docker/WSL/a devcontainer? Multicast doesn't work there;
            run this sample on the host.
          - On Windows, the "SSDP Discovery" service (SSDPSRV) occupies UDP 1900 and
            keeps clients from seeing responses. Pause it while discovering
            (elevated prompt): net stop SSDPSRV - and resume after: net start SSDPSRV
          - Try Sample.Browser to see whether *any* UPnP device answers at all.
        """);
    return;
}

Write(ConsoleColor.DarkGray, "Gateway:     ");
WriteLine(ConsoleColor.Yellow, gateway.Device.Description.FriendlyName ?? "(unnamed)");
Write(ConsoleColor.DarkGray, "Service:     ");
Console.WriteLine(gateway.WanConnectionService.Description.ServiceType);

var status = await gateway.GetStatusInfoAsync();
Write(ConsoleColor.DarkGray, "WAN:         ");
Write(status.IsConnected ? ConsoleColor.Green : ConsoleColor.Red, status.Status ?? "(unknown)");
Console.WriteLine($"  (uptime {status.Uptime}, last error {status.LastError})");

Write(ConsoleColor.DarkGray, "External IP: ");
WriteLine(ConsoleColor.Green, (await gateway.GetExternalIPAddressAsync()).ToString());

Console.WriteLine();
Console.WriteLine("Current port mappings:");

await foreach (var mapping in gateway.GetPortMappingsAsync())
{
    Write(ConsoleColor.Cyan, $"  {mapping.Protocol.ToWireString(),-4}");
    Write(ConsoleColor.White, $"{mapping.ExternalPort,5}");
    Write(ConsoleColor.DarkGray, " -> ");
    Write(ConsoleColor.White, $"{mapping.InternalClient}:{mapping.InternalPort}");
    WriteLine(ConsoleColor.DarkGray, $"  lease {mapping.LeaseDuration}  \"{mapping.Description}\"");
}

if (args.Length > 0 && args[0] == "--map")
{
    var taken = await gateway.GetSpecificPortMappingEntryAsync(18080, Protocol.Tcp);

    if (taken is not null)
    {
        WriteLine(ConsoleColor.Yellow,
            $"\nExternal port 18080 is already mapped to {taken.InternalClient} (\"{taken.Description}\").");
        return;
    }

    Console.WriteLine("\nMapping TCP 18080 -> 18080 for 15 minutes (auto-renewing)...");

    await using var lease = await gateway.AddPortMappingAsync(
        externalPort: 18080, internalPort: 18080, Protocol.Tcp,
        description: "UPnP.Rx sample", lease: TimeSpan.FromMinutes(15));

    using var events = lease.Events.Subscribe(e =>
        WriteLine(
            e.Kind switch
            {
                PortMappingEventKind.Renewed => ConsoleColor.Green,
                PortMappingEventKind.RenewalFailed => ConsoleColor.Yellow,
                _ => ConsoleColor.Red
            },
            $"  [lease] {e.Kind}{(e.Message is null ? "" : $": {e.Message}")}"));

    Write(ConsoleColor.Green, $"Mapped external port {lease.Mapping.ExternalPort}. ");
    Console.WriteLine("Press Enter to release.");
    Console.ReadLine();
}

static void Write(ConsoleColor color, string text)
{
    var previous = Console.ForegroundColor;
    Console.ForegroundColor = color;
    Console.Write(text);
    Console.ForegroundColor = previous;
}

static void WriteLine(ConsoleColor color, string text)
{
    Write(color, text);
    Console.WriteLine();
}
