using System.Text;
using UPnP.Rx;
using UPnP.Rx.Model;

// Sample.Browser - discover every UPnP device on the network and dump its
// device tree and services. Requires a real network; multicast does not work
// in containers.
//
// Rendering notes: colors via Console.ForegroundColor (portable, no ANSI
// escapes); glyphs restricted to code page 437 (box-drawing + middle dot) so
// even the legacy Windows console renders them.

try
{
    Console.OutputEncoding = Encoding.UTF8;   // modern terminals; harmless if it sticks
}
catch (Exception)
{
    // Legacy console: the CP437-safe glyph set below still renders fine.
}

var addresses = LocalNetwork.IPv4Addresses();

if (addresses.Length is 0)
{
    WriteLine(ConsoleColor.Red, "No usable IPv4 interfaces found.");
    return;
}

Write(ConsoleColor.Cyan, "UPnP.Rx");
Console.WriteLine(" network browser");
Write(ConsoleColor.DarkGray, "Browsing from: ");
Console.WriteLine(string.Join(", ", addresses.Select(a => a.ToString())));
Write(ConsoleColor.DarkGray, "Discovering (press Enter to stop)...");
Console.WriteLine();
Console.WriteLine();

using var upnp = new UpnpClient(addresses);

var found = 0;

// One line from discovery to described device trees; failures are skipped
// (pass an ILogger via UpnpClientOptions to see them).
using var subscription = upnp
    .DiscoverDescribedDevices()
    .Subscribe(device =>
    {
        Interlocked.Increment(ref found);
        Write(ConsoleColor.Yellow, device.Description.FriendlyName ?? "(unnamed device)");
        WriteLine(ConsoleColor.DarkGray, $"  [{device.Description.Location}]");
        PrintDeviceLine(device.Description, connector: "", childPrefix: "");
        Console.WriteLine();
    });

await Task.Delay(TimeSpan.FromSeconds(15), TimeProvider.System);

if (Volatile.Read(ref found) is 0)
{
    WriteLine(ConsoleColor.Yellow, """
        Nothing answered in 15 seconds. Things to check:
          - Running inside Docker/WSL/a devcontainer? Multicast doesn't work there;
            run this sample on the host.
          - Is a VPN active? Try disconnecting.
          - On Windows, the "SSDP Discovery" service (SSDPSRV) occupies UDP 1900 and
            keeps clients from seeing responses. Pause it while discovering
            (elevated prompt): net stop SSDPSRV - and resume after: net start SSDPSRV
          - Some networks block SSDP (AP isolation, IGMP snooping) - try another
            network or a wired connection.
        Still listening - devices announce themselves periodically...
        """);
}

Console.ReadLine();

Write(ConsoleColor.Cyan, $"{Volatile.Read(ref found)}");
Console.WriteLine(" device(s) found.");

static void PrintDeviceLine(DeviceDescription device, string connector, string childPrefix)
{
    Write(ConsoleColor.DarkGray, connector);
    Write(ConsoleColor.Gray, device.DeviceType ?? "(no deviceType)");

    var maker = $"{device.Manufacturer} {device.ModelName}".Trim();
    WriteLine(ConsoleColor.DarkGray, maker.Length is 0 ? "" : $"  ({maker})");

    // Services first, then embedded devices, with proper tree connectors.
    var lastIndex = device.Services.Count + device.EmbeddedDevices.Count - 1;
    var index = 0;

    foreach (var service in device.Services)
    {
        var isLast = index++ == lastIndex;
        Write(ConsoleColor.DarkGray, childPrefix + (isLast ? "└─ " : "├─ "));
        WriteLine(ConsoleColor.DarkCyan, $"· {service.ServiceType}");
    }

    foreach (var embedded in device.EmbeddedDevices)
    {
        var isLast = index++ == lastIndex;
        PrintDeviceLine(
            embedded,
            childPrefix + (isLast ? "└─ " : "├─ "),
            childPrefix + (isLast ? "   " : "│  "));
    }
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

// ===========================================================================
// ANALYZER DEMO - the options rule (UPNPRX002) and the rules UPnP.Rx inherits
// from its upstream packages (SSDP001, SSDP003, SHLRX001).
//
// Commented out on purpose: this repo builds with TreatWarningsAsErrors.
// To try one: select a numbered block - the code only - and uncomment it
// (Ctrl+K Ctrl+U), then `dotnet build samples/Sample.Browser`.
//
// The port-mapping rules (UPNPRX001, UPNPRX003) are demonstrated in
// samples/Sample.PortMapper/Program.cs, where the API they guard lives.
// ===========================================================================

#pragma warning disable IDE0051, CS8321 // demo helpers are deliberately unused

// --- (1) UPNPRX002: a UpnpClientOptions duration outside its range ---------

// static UpnpClientOptions Upnprx002_Reported() => new()
// {
//     ActionTimeout = TimeSpan.Zero,
//     DescriptionTimeout = TimeSpan.FromSeconds(-1),
//     RosterExpiryFallback = TimeSpan.Zero,
//     EventSubscriptionTimeout = TimeSpan.FromMilliseconds(500)
// };

// The same rule staying quiet. Short-but-positive may be exactly right on a
// fast LAN, and a rule that argues with a legitimate choice is one you learn to
// suppress - taking the quiet rules with it. EventCallbackPort needs no rule at
// all: it is a ushort, so there is no out-of-range value to write.

// static UpnpClientOptions Upnprx002_Silent() => new()
// {
//     ActionTimeout = TimeSpan.FromMilliseconds(100),
//     EventCallbackPort = 49152
// };

// --- (2) SSDP001 / SSDP003: inherited from SSDP.UPnP.PCL -------------------
// Nothing extra is referenced to get these. A package's analyzers reach you
// through the package that depends on it - here, through UPnP.Rx.

// static void Inherited_Reported()
// {
//     var mx = new SSDP.UPnP.PCL.Model.MxSeconds(30);
//     var port = new SSDP.UPnP.PCL.Model.DynamicPort(80);
// }

// That MX ceiling reaching YOUR call site is exactly why DiscoverDevices takes
// MxSeconds rather than TimeSpan: the ">= 1" the spec mandates is a type
// invariant, and the "<= 5" it merely recommends is SSDP001's job, on your
// literal, at your call site. One rule this library never had to write.

// static void Inherited_ViaOurApi(IUpnpClient client) =>
//     client.DiscoverDevices(mx: new SSDP.UPnP.PCL.Model.MxSeconds(30)).Subscribe(_ => { });

// --- (3) SHLRX001: from SimpleHttpListener.Rx, three package hops away -----
// Subscribe(async …) compiles to async void: exceptions bypass OnError,
// ordering is not preserved, nothing applies backpressure. House Rx rule 1,
// enforced by somebody else's analyzer, and it still arrives.

// static IDisposable Shlrx001_Reported(IObservable<DiscoveredDevice> devices) =>
//     devices.Subscribe(async device => await device.GetDescriptionAsync());

#pragma warning restore IDE0051, CS8321
