# UPnP.Rx

[![NuGet](https://img.shields.io/nuget/v/UPnP.Rx.svg)](https://www.nuget.org/packages/UPnP.Rx)

A modern, functional, Rx-based **UPnP control point** for .NET 10: discover devices, browse their services, call their actions — as observables and immutable records. Includes an **IGD port-mapping client** with auto-renewing leases.

> *Discover a device, browse its services, call its actions, watch its state.*

## Overview

UPnP.Rx covers the full control-point chain of the UPnP Device Architecture 2.0:

- **Discovery** (UDA clause 1) — SSDP via [SSDP.UPnP.PCL](https://www.nuget.org/packages/SSDP.UPnP.PCL), exposed as observable streams of discovered devices.
- **Description** (clause 2) — device description documents and SCPDs fetched lazily, parsed into immutable records, cached by `LOCATION` + `CONFIGID`.
- **Control** (clause 3) — SOAP 1.1 action calls with typed results and typed UPnP faults.
- **Port mapping** — the flagship: find the internet gateway and map ports in one call, with automatic lease renewal.

Eventing (clause 4, GENA) is planned for v2.

## Installing

```
dotnet add package UPnP.Rx
```

## Try it in two minutes

No code needed — clone and run the browser against your own network:

```bash
git clone https://github.com/1iveowl/UPnP.rx.git
cd UPnP.rx
dotnet run --project samples/Sample.Browser
```

```
● FRITZ!Box 7590  [http://192.168.178.1:49000/igddesc.xml]
  urn:schemas-upnp-org:device:InternetGatewayDevice:2  (AVM FRITZ!Box 7590)
    ⚙ urn:schemas-upnp-org:service:WANCommonInterfaceConfig:1
    …
```

`dotnet run --project samples/Sample.PortMapper` finds your router and lists its
port-mapping table; add `--map` to hold an auto-renewing mapping. (Run on the
host, not in a container — see Troubleshooting.)

## Quick start — port mapping

```csharp
using UPnP.Rx.PortMapping;

// One line: discover the gateway, map the port, auto-renew the lease.
await using var lease = await PortMapper.AddPortMappingAsync(
    externalPort: 8080, internalPort: 8080, Protocol.Tcp,
    description: "my app", lease: TimeSpan.FromHours(1));

Console.WriteLine($"Mapped external port {lease.Mapping.ExternalPort}");

// Renewal outcomes are an observable — a failed renewal retries, it never throws.
using var events = lease.Events.Subscribe(e => Console.WriteLine($"[lease] {e.Kind}"));
```

Disposing the lease with `await using` removes the mapping from the router. Sync `Dispose()` is the abrupt path: renewal stops and the finite lease simply expires on the router — you never leak a mapping forever.

More control:

```csharp
await using var gateway = await PortMapper.DiscoverGatewayAsync();

Console.WriteLine(await gateway!.GetExternalIPAddressAsync());
Console.WriteLine((await gateway.GetStatusInfoAsync()).IsConnected);        // WAN up?

var taken = await gateway.GetSpecificPortMappingEntryAsync(8080, Protocol.Tcp);
Console.WriteLine(taken is null ? "8080 is free" : $"8080 -> {taken.InternalClient}");

await foreach (var m in gateway.GetPortMappingsAsync())
    Console.WriteLine($"{m.Protocol} {m.ExternalPort} -> {m.InternalClient}:{m.InternalPort}");
```

Rx-first? The scalar discovery is `FirstAsync` sugar over an observable — subscribe to the stream itself (multi-homed networks can have several gateways):

```csharp
using var upnp = new UpnpClient(myAddresses);
using var gateways = PortMapper.DiscoverGateways(upnp).Subscribe(g =>
    Console.WriteLine(g.Device.Description.FriendlyName));
```

## Quick start — the general client

```csharp
using UPnP.Rx;

using var upnp = new UpnpClient(ipAddress);            // your local interface address(es)

using var subscription = upnp
    .DiscoverDescribedDevices()                        // SSDP + description fetch, cached
    .Where(d => d.HasService("WANIPConnection"))
    .SelectMany(async gateway =>
    {
        var wan = gateway.Service("WANIPConnection");
        return await wan.InvokeAsync("GetExternalIPAddress");
    })
    .Subscribe(result => Console.WriteLine(result["NewExternalIPAddress"]));
```

(`DiscoverDevices()` gives the raw discovery stream — SSDP envelopes with lazy
`GetDescriptionAsync()` — when you want control over the description step.)

- `DiscoverDevices(searchTarget, mx)` sends an M-SEARCH on subscription and merges `ssdp:alive` announcements, deduplicated per subscription. The default target is `upnp:rootdevice`; configure it via `UpnpClientOptions.DefaultSearchTarget` or per call (`SearchTargets.All`, `SearchTargets.DeviceType("MediaRenderer")`, …).
- `DeviceLost()` streams `ssdp:byebye` notices.
- `Service(...)` matches by full service type URN, service id, or bare type name (`"WANIPConnection"` matches any version), across the whole embedded-device tree.
- `InvokeAsync` throws `UpnpActionException` carrying the device's `UpnpError` on SOAP faults.

## Behavior notes

- **Strict in what we send, lenient in what we accept.** Envelopes and headers follow the UDA 2.0 letter (including the quoted `charset="utf-8"`); parsers tolerate wrong namespaces, wrong casing, whitespace inside identifiers, unescaped ampersands, and UPnP 1.0-era `URLBase` (which UDA 2.0 requires control points to honor). A document only fails to parse when it identifies nothing.
- **Pipelines never die from one bad message.** Degraded announcements are surfaced (`DiscoveredDevice.HasParsingError`), unusable ones are dropped with a log note; `OnError` is reserved for the source itself dying.
- **One clock.** Every timeout and renewal runs on `UpnpClientOptions.TimeProvider` (default `TimeProvider.System`) — inject `FakeTimeProvider` in tests and drive renewals deterministically.
- **Disposal.** `DisposeAsync` is the graceful path (deletes port mappings; will unsubscribe eventing in v2); sync `Dispose` releases resources without network goodbyes.
- **Spec review.** Clause 2/3 behavior was audited against the UDA 2.0 text; the findings live in [plan/uda2-compliance-review.md](plan/uda2-compliance-review.md).

## Why not …?

| | Covers | State | UPnP.Rx difference |
|---|---|---|---|
| **Open.NAT** | Port mapping only | Unmaintained since 2016 | Full discover→describe→control chain, maintained, modern .NET |
| **Mono.Nat** | Port mapping (UPnP + NAT-PMP) | Dormant since 2022, callback API | Rx + `async` API, auto-renewing leases, description/control beyond IGD |
| **Rssdp** | SSDP discovery only | Active | Rssdp hands you a LOCATION URL; UPnP.Rx continues from there |
| **Waher.Networking.UPnP** | Discovery + some control | Part of a large framework | Standalone, near-zero dependencies, net10.0-idiomatic |

Known boundary: UPnP.Rx speaks UPnP IGD only — no NAT-PMP/PCP (Mono.Nat covers
those if you need them). Planned next: GENA eventing (v2), a live device roster
with expiry (v1.1).

## Troubleshooting

**No gateway / no devices found?**

- **UPnP is often disabled on routers** — look for "UPnP"/"IGD" under advanced
  or NAT settings in the router UI.
- **Containers can't multicast**: Docker, WSL and devcontainers won't see SSDP.
  Run on the host.
- **VPNs** commonly capture the default route or block multicast — try
  disconnected.
- **AP isolation / IGMP snooping** on some networks filters SSDP — try wired.
- `Sample.Browser` answers "is *anything* visible from this machine?" in one
  command; `Sample.PortMapper` prints the interfaces it searched from.
- Pass an `ILogger` via `UpnpClientOptions.Logger` to see dropped announcements
  and skipped descriptions.

## Advanced

Bring your own SSDP control point (for interception, custom sockets, or tests) and/or `HttpClient`:

```csharp
var controlPoint = new ControlPoint(myPreparedInterfaces);   // SSDP.UPnP.PCL
using var upnp = new UpnpClient(controlPoint, myHttpClient, options, addresses);
```

Tests fake the network at two seams: `IControlPoint` (drive parsed SSDP messages from a `Subject`) and `HttpMessageHandler` (serve descriptions and SOAP). Multicast is never required. For **your own** unit tests, the control surfaces have interfaces — `IUpnpService`, `IInternetGateway`, `IPortMappingLease` — so application code can be tested against fakes without any network replay.

SCPD-driven argument marshalling — validate and order in-arguments before invoking:

```csharp
var scpd = await wan.GetScpdAsync();
var args = scpd.ValidateAndOrderArguments("AddPortMapping", myArguments);
// args.IsSuccess ? await wan.InvokeAsync("AddPortMapping", args.Value) : report args.Error
```

## Samples

- [`samples/Sample.PortMapper`](samples/Sample.PortMapper) — discover the gateway, print the external IP and mapping table; `--map` holds an auto-renewing mapping.
- [`samples/Sample.Browser`](samples/Sample.Browser) — discover everything on the network and dump device trees and services.

Both need a real network (multicast does not work in containers).

## Version history

| Version | Notes |
|---|---|
| 1.0.0 | Initial release: discovery → description → control, IGD port mapping with auto-renewing leases. |

## Why .NET 10?

UPnP.Rx is `net10.0`-only, like its siblings [SSDP.UPnP.PCL](https://github.com/1iveowl/SSDP.UPnP.PCL) and [SimpleHttpListener.Rx](https://github.com/1iveowl/SimpleHttpListener.Rx): modern C# records for the immutable model, `TimeProvider` throughout for testable time, and no legacy TFM baggage. If you need older targets, the underlying protocol layers remain available separately.

## License

MIT — see [LICENSE](LICENSE).
