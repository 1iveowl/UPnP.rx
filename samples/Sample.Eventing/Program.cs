using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reactive.Linq;
using UPnP.Rx;
using UPnP.Rx.Eventing;

// Sample.Eventing - subscribe to a service's evented state and print every
// UpnpEvent. The author's integration-test protocol lives in
// plan/upnp-rx-v4.0-eventing-plan.md §5.
//
// Usage: dotnet run --project samples/Sample.Eventing [-- --timeout <seconds>]
// Requires a real network (multicast does not work in containers); expect a
// firewall prompt for the callback listener on first run.

var timeoutSeconds = 1800;
for (var i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--timeout" && int.TryParse(args[i + 1], out var t))
    {
        timeoutSeconds = t;
    }
}

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

Write(ConsoleColor.Cyan, "UPnP.Rx");
Console.WriteLine($" eventing sample  (subscription timeout: {timeoutSeconds}s)");
WriteLine(ConsoleColor.DarkGray, "Discovering evented services (8 s)...");

using var upnp = new UpnpClient(
    new UpnpClientOptions { EventSubscriptionTimeout = TimeSpan.FromSeconds(timeoutSeconds) },
    addresses);

var candidates = new List<(string Device, UpnpService Service)>();

using (upnp.DiscoverDescribedDevices().Subscribe(device =>
{
    lock (candidates)
    {
        foreach (var service in device.Services.Where(s => s.Description.EventSubUrl is not null))
        {
            candidates.Add((device.Description.FriendlyName ?? "(unnamed)", service));
        }
    }
}))
{
    await Task.Delay(TimeSpan.FromSeconds(8), TimeProvider.System);
}

List<(string Device, UpnpService Service)> found;
lock (candidates)
{
    found = [.. candidates];
}

if (found.Count is 0)
{
    WriteLine(ConsoleColor.Yellow,
        "No evented services found. Container? VPN? Windows SSDPSRV? See the README's Troubleshooting.");
    return;
}

for (var i = 0; i < found.Count; i++)
{
    Console.Write($"  [{i}] ");
    Write(ConsoleColor.Yellow, found[i].Device);
    Console.WriteLine($"  {found[i].Service.Description.ServiceType}");
}

Console.Write("Pick a service number: ");

if (!int.TryParse(Console.ReadLine(), out var pick) || pick < 0 || pick >= found.Count)
{
    Console.WriteLine("No valid pick; exiting.");
    return;
}

var chosen = found[pick].Service;
Console.WriteLine();
Write(ConsoleColor.Cyan, "Subscribing");
Console.WriteLine($" to {chosen.Description.ServiceType} - press Enter to unsubscribe gracefully.");
Console.WriteLine();

using var subscription = chosen.Events().Subscribe(
    e =>
    {
        switch (e)
        {
            case PropertyChange { IsReplay: true } r:
                WriteLine(ConsoleColor.DarkGray, $"  (replay)  {r.Name} = {Trim(r.Value)}");
                break;
            case PropertyChange { IsInitialState: true } i:
                WriteLine(ConsoleColor.DarkYellow, $"  (initial) {i.Name} = {Trim(i.Value)}");
                break;
            case PropertyChange c:
                Console.WriteLine($"  [{c.Seq}] {c.Name} = {Trim(c.Value)}");
                break;
            case Subscribed s:
                WriteLine(ConsoleColor.Cyan, $"  SUBSCRIBED  sid={s.Sid}  timeout={s.Timeout}");
                break;
            case Resubscribed rs:
                WriteLine(ConsoleColor.Cyan, $"  RESUBSCRIBED  sid={rs.Sid}");
                break;
            case RenewalFailed f:
                WriteLine(ConsoleColor.Yellow, $"  RENEWAL FAILED  {f.Message}");
                break;
            case GapDetected g:
                WriteLine(ConsoleColor.Red, $"  SEQ GAP  expected {g.ExpectedSeq}, got {g.ActualSeq}");
                break;
            case SubscriptionRefused refused:
                WriteLine(ConsoleColor.Red, $"  REFUSED (HTTP {refused.HttpStatus})  {refused.Reason}");
                break;
        }
    },
    error => WriteLine(ConsoleColor.Red, $"  STREAM ERROR  {error.Message}"));

Console.ReadLine();
Console.WriteLine("Unsubscribing (graceful)...");

static string Trim(string value) =>
    value.Length <= 120 ? value : value[..120] + "…";

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
