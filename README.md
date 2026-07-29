# UPnP.Rx

[![NuGet](https://img.shields.io/nuget/v/UPnP.Rx?logo=nuget&label=UPnP.Rx)](https://www.nuget.org/packages/UPnP.Rx)
[![Downloads](https://img.shields.io/nuget/dt/UPnP.Rx?logo=nuget&color=blue)](https://www.nuget.org/packages/UPnP.Rx)
[![CI](https://github.com/1iveowl/UPnP.rx/actions/workflows/ci.yml/badge.svg)](https://github.com/1iveowl/UPnP.rx/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)

[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![System.Reactive](https://img.shields.io/badge/Rx-7.0-ff69b4.svg)](https://reactivex.io/)
[![UPnP](https://img.shields.io/badge/UPnP%20Device%20Architecture-2.0-2563EB.svg)](http://upnp.org/specs/arch/UPnP-arch-DeviceArchitecture-v2.0.pdf)
[![Analyzers](https://img.shields.io/badge/analyzers-3%20rules%20%2B%20generator-8A2BE2.svg)](#build-time-checks)
[![Trim / AOT](https://img.shields.io/badge/trim%20%2F%20native%20AOT-verified-success.svg)](#trimming-and-native-aot)

A modern, functional, Rx-based **UPnP control point** for .NET 10: discover devices, browse their services, call their actions - as observables and immutable records. Includes an **IGD port-mapping client** with auto-renewing leases.

> *Discover a device, browse its services, call its actions, watch its state, change its state.*

*Please star this project if you find it useful. Thank you.*

## Overview

UPnP.Rx covers the full control-point chain of the [UPnP Device Architecture 2.0](https://upnp.org/specs/arch/UPnP-arch-DeviceArchitecture-v2.0-20140901.pdf):

- **Discovery** (UDA clause 1) - SSDP via [SSDP.UPnP.PCL](https://www.nuget.org/packages/SSDP.UPnP.PCL), exposed as observable streams of discovered devices.
- **Description** (clause 2) - device description documents and SCPDs fetched lazily, parsed into immutable records, cached by `LOCATION` + `CONFIGID`.
- **Control** (clause 3) - SOAP 1.1 action calls with typed results and typed UPnP faults.
- **Port mapping** - the flagship: find the internet gateway and map ports in one call, with automatic lease renewal.
- **Eventing** (clause 4, GENA) - subscribe to a service's evented state as an observable: `service.Events()` handles SUBSCRIBE/renewal/UNSUBSCRIBE, replays last-known state to late subscribers, and recovers from failures and SEQ gaps automatically. A subscription also watches its device's presence, so a byebye or an unannounced reboot abandons it with `SubscriptionCancelled` and a reason instead of leaving it silently dead until a renewal fails (UDA 2.0 clause 4.1.1). AV services' `LastChange` payloads decode via `UPnP.Rx.Eventing.Av` (`events.SelectAvChanges()`).
- **Roster** - `client.Roster()` (in `UPnP.Rx.Presence`) streams device presence as changes: arrivals, restarts (`DeviceRebooted`, including UPnP 1.0 devices, which signal one with `NLS` rather than `BOOTID`), description changes under a device that stayed up, `CACHE-CONTROL`-driven expiry for devices that vanish silently, and byebye departures - with the current roster replayed to late subscribers. Bounded state, built for long-lived apps. Alongside it: `Announcements()` streams every parsed SSDP envelope undeduplicated (the activity-log feed), and `SearchAsync()` sends one M-SEARCH burst to solicit fresh responses without resetting anything.
- **Version claims** - `UpnpVersionClaims` reports what a device says about the UDA version it implements in each of the four places UDA 2.0 makes normative (the `SERVER` header, the device description, each SCPD, and control responses). The spec names no authority between them, so the claims are kept with their provenance rather than reconciled away, and a device that contradicts itself is visible as exactly that.

## Installing

```
dotnet add package UPnP.Rx
```

## Try it in two minutes

No code needed - clone and run the browser against your own network:

```bash
git clone https://github.com/1iveowl/UPnP.rx.git
cd UPnP.rx
dotnet run --project samples/Sample.Browser
```

```
FRITZ!Box 7590  [http://192.168.178.1:49000/igddesc.xml]
urn:schemas-upnp-org:device:InternetGatewayDevice:2  (AVM FRITZ!Box 7590)
├─ · urn:schemas-upnp-org:service:WANCommonInterfaceConfig:1
└─ urn:schemas-upnp-org:device:WANDevice:2  (AVM)
   └─ ...
```

(colored in the terminal; glyphs chosen to render on every platform, including
the legacy Windows console)

`dotnet run --project samples/Sample.PortMapper` finds your router and lists its
port-mapping table; add `--map` to hold an auto-renewing mapping. Run on the
host, not in a container - and **on Windows, pause the built-in "SSDP Discovery"
service first**, since it occupies UDP 1900 and keeps clients from seeing
responses (elevated prompt):

```cmd
net stop SSDPSRV     &REM pause - lets UPnP.Rx receive SSDP
net start SSDPSRV    &REM resume when done
```

More gotchas under [Troubleshooting](#troubleshooting).

## Ever wondered what UPnP devices are on your network?

The dashboard sample answers it live: every device on your LAN, unfolded down
to its services and actions. Watch a Sonos change state in real time, drag its
volume from the browser, follow the SSDP chatter per device - or invoke any
action its SCPD declares, with a form generated from the spec itself.

Leave it running and it becomes a record rather than a snapshot: devices that go
stay listed, grayed, saying whether they announced their departure or simply went
quiet, and when. Restarts are called out, each device shows what UDA version it
claims and flags itself when those claims disagree, and a deep search asks
`ssdp:all` for the hardware that ignores an ordinary root-device search.

```bash
dotnet run --project samples/Sample.Dashboard      # on the host, then open http://localhost:5287
```

![The dashboard: a Sonos Five unfolded - live eventing, transport controls and 42 invocable actions](https://raw.githubusercontent.com/1iveowl/UPnP.rx/refs/heads/main/assets/dashboard.png)

*Blazor WebAssembly + FluentUI + ReactiveUI, light and dark - and everything in
it is built on the library's public API.*

## Quick start - port mapping

```csharp
using UPnP.Rx.PortMapping;

// One line: discover the gateway, map the port, auto-renew the lease.
await using var lease = await PortMapper.AddPortMappingAsync(
    externalPort: 8080, internalPort: 8080, Protocol.Tcp,
    description: "my app", lease: TimeSpan.FromHours(1));

Console.WriteLine($"Mapped external port {lease.Mapping.ExternalPort}");

// Renewal outcomes are an observable - a failed renewal retries, it never throws.
using var events = lease.Events.Subscribe(e => Console.WriteLine($"[lease] {e.Kind}"));
```

Disposing the lease with `await using` removes the mapping from the router. Sync `Dispose()` is the abrupt path: renewal stops and the finite lease simply expires on the router - you never leak a mapping forever.

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

Rx-first? The scalar discovery is `FirstAsync` sugar over an observable - subscribe to the stream itself (multi-homed networks can have several gateways):

```csharp
using var upnp = new UpnpClient(myAddresses);
using var gateways = PortMapper.DiscoverGateways(upnp).Subscribe(g =>
    Console.WriteLine(g.Device.Description.FriendlyName));
```

## Quick start - the general client

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

(`DiscoverDevices()` gives the raw discovery stream - SSDP envelopes with lazy
`GetDescriptionAsync()` - when you want control over the description step.)

- `DiscoverDevices(searchTarget, mx)` sends an M-SEARCH on subscription and merges `ssdp:alive` announcements, deduplicated per subscription. The default target is `upnp:rootdevice`; configure it via `UpnpClientOptions.DefaultSearchTarget` or per call (`SearchTargets.All`, `SearchTargets.DeviceType("MediaRenderer")`, …).
- `DeviceLost()` streams `ssdp:byebye` notices.
- `Service(...)` matches by full service type URN, service id, or bare type name (`"WANIPConnection"` matches any version), across the whole embedded-device tree.
- `InvokeAsync` throws `UpnpActionException` carrying the device's `UpnpError` on SOAP faults.

## Behavior notes

- **Strict in what we send, lenient in what we accept.** Envelopes and headers follow the UDA 2.0 letter (including the quoted `charset="utf-8"`); parsers tolerate wrong namespaces, wrong casing, whitespace inside identifiers, unescaped ampersands, and UPnP 1.0-era `URLBase` (which UDA 2.0 requires control points to honor). A document only fails to parse when it identifies nothing.
- **Pipelines never die from one bad message.** Degraded announcements are surfaced (`DiscoveredDevice.HasParsingError`), unusable ones are dropped with a log note; `OnError` is reserved for the source itself dying.
- **One clock.** Every timeout and renewal runs on `UpnpClientOptions.TimeProvider` (default `TimeProvider.System`) - inject `FakeTimeProvider` in tests and drive renewals deterministically.
- **Disposal.** `DisposeAsync` is the graceful path: port-mapping leases delete their mappings, and a client with live event subscriptions sends UNSUBSCRIBE before releasing its resources; disposing an `Events()` subscription likewise says goodbye from the engine's own teardown. Sync `Dispose` releases resources without network goodbyes - finite lease and subscription timeouts make that safe (the device expires them on its own).
- **Spec review.** Clause 2/3 and clause 4 behavior was audited against the UDA 2.0 text; the findings live in [plan/uda2-compliance-review.md](plan/uda2-compliance-review.md) and [plan/uda2-clause4-compliance-review.md](plan/uda2-clause4-compliance-review.md).

## Where UPnP.Rx fits

The .NET ecosystem has several UPnP libraries, each with real strengths.
[Mono.Nat](https://github.com/alanmcgovern/Mono.Nat) and Open.NAT made router
port mapping accessible to a generation of .NET apps - and Mono.Nat also speaks
NAT-PMP, which UPnP.Rx deliberately leaves to it.
[Rssdp](https://github.com/Yortw/RSSDP) is a focused, actively maintained SSDP
implementation with device-side publishing.
[Waher.Networking.UPnP](https://github.com/PeterWaher/IoTGateway) brings UPnP
into a much broader IoT framework. If one of those matches your needs and
target frameworks, it's a fine choice.

UPnP.Rx's place is the **full control-point chain in one standalone package** -
discover → describe → control - for modern .NET:

- an Rx + immutable-records API (device presence and lease renewals as observables),
- `async`/`IAsyncEnumerable` ergonomics, one testable clock (`TimeProvider`) throughout,
- spec-audited UDA 2.0 behavior with deliberately lenient parsing of real-world devices,
- port mapping with auto-renewing leases as the flagship, and
- near-zero dependencies on `net10.0`.

At a glance:

| Library | Focus | UPnP.Rx in comparison |
|---|---|---|
| **Mono.Nat** | Port mapping (UPnP IGD + NAT-PMP) | UPnP only, but adds description/control beyond IGD, an Rx + `async` API, and auto-renewing leases |
| **Open.NAT** | Port mapping (UPnP IGD) | The full discover → describe → control chain, under active development |
| **Rssdp** | SSDP discovery + device-side publishing | Picks up where discovery ends: from the LOCATION URL to description and control |
| **Waher.Networking.UPnP** | UPnP within the Waher IoT framework | Standalone package, near-zero dependencies, `net10.0`-idiomatic |

Known boundary: UPnP.Rx speaks UPnP only - for NAT-PMP/PCP, Mono.Nat has you
covered. Planned next: a live device roster with expiry (4.1).

## Troubleshooting

**No gateway / no devices found?**

- **UPnP is usually disabled on gateways by default** - and that only affects
  the *port-mapping* features; discovery and control of other devices (TVs,
  speakers, hubs) work regardless. On **UniFi** (UDM/UXG/Express/USG - they run
  `miniupnpd`): *Settings → Internet → your WAN → UPnP* (older controllers:
  *Settings → Services → UPnP*), primary WAN only. Other routers/firewalls hide
  the toggle under NAT, port forwarding or "media sharing" settings. Security
  note: the IGD control endpoint is LAN-side only by design - nothing on the
  WAN can reach it - but it has no authentication, so *any LAN device* can open
  WAN ports for itself. Enable it consciously; the mapping table (dashboard or
  `Sample.PortMapper`) shows exactly what has been opened, and UPnP.Rx's own
  mappings use finite auto-renewed leases that expire on their own.
- **Containers can't multicast**: Docker, WSL and devcontainers won't see SSDP.
  Run on the host.
- **On Windows, the built-in "SSDP Discovery" service (`SSDPSRV`) occupies
  UDP 1900** and keeps other clients from seeing responses. Pause it while
  discovering (elevated prompt), and start it again afterwards:

  ```cmd
  net stop SSDPSRV     &REM pause - lets UPnP.Rx receive SSDP
  net start SSDPSRV    &REM resume when done
  ```
- **VPNs** commonly capture the default route or block multicast - try
  disconnected.
- **AP isolation / IGMP snooping** on some networks filters SSDP - try wired.
- `Sample.Browser` answers "is *anything* visible from this machine?" in one
  command; `Sample.PortMapper` prints the interfaces it searched from.
- Pass an `ILogger` via `UpnpClientOptions.Logger` to see dropped announcements
  and skipped descriptions.

**Eventing (`Events()`) trouble?**

- **The callback listener needs an inbound port.** Subscribing starts a small
  HTTP listener for the device's NOTIFYs - expect a firewall prompt on first
  use and allow it. By default the port is ephemeral; set
  `UpnpClientOptions.EventCallbackPort` to a fixed port if you need a firewall
  rule.
- **`SubscriptionRefused` with HTTP 404/405/410/501**: some devices advertise
  an `eventSubURL` for services that are not actually evented - either the
  endpoint refuses the method (405/501, e.g. Sonos's `QPlay:1`) or the URL is a
  placeholder that plain doesn't exist (404/410, e.g. the `/ssdp/notfound`
  stub on Sonos's `SpeakerGroup:1` pseudo-device). Such a refusal contradicts
  the device's own description and is permanent, so instead of retrying
  forever the stream reports the reason as a `SubscriptionRefused` event and
  then terminates with `OnError`. Transient failures (timeouts, 5xx during a
  reboot, SEQ gaps) keep the auto-recovery behavior.

## Advanced

Bring your own SSDP control point (for interception, custom sockets, or tests) and/or `HttpClient`:

```csharp
var controlPoint = new ControlPoint(myPreparedInterfaces);   // SSDP.UPnP.PCL
using var upnp = new UpnpClient(controlPoint, myHttpClient, options, addresses);
```

Tests fake the network at two seams: `IControlPoint` (drive parsed SSDP messages from a `Subject`) and `HttpMessageHandler` (serve descriptions and SOAP). Multicast is never required. For **your own** unit tests, the control surfaces have interfaces - `IUpnpService`, `IInternetGateway`, `IPortMappingLease` - so application code can be tested against fakes without any network replay.

SCPD-driven argument marshalling - validate and order in-arguments before invoking:

```csharp
var scpd = await wan.GetScpdAsync();
var args = scpd.ValidateAndOrderArguments("AddPortMapping", myArguments);
// args.IsSuccess ? await wan.InvokeAsync("AddPortMapping", args.Value) : report args.Error
```

## Build-time checks

UPnP.Rx ships analyzers inside the package - no extra reference, nothing to install. They
report mistakes this library can see at build time instead of letting a router, or a silent
default, answer for them.

The design rule is that a **rule is the last resort**. Most of what an analyzer could have
checked here is instead unrepresentable: ports are `ushort`, `Protocol` is an `enum`, MX is
`MxSeconds`, so the compiler refuses the bad value without anyone writing a diagnostic. What
remains are range and resource mistakes the type system cannot express.

Each rule reports **literals and compile-time constants only**. A computed value is left
alone deliberately - the budget is zero false positives, and a run-time guard covers the rest.
A rule you learn to suppress takes the quiet ones with it.

**Turning one off** - `.editorconfig`, `<NoWarn>` and `#pragma warning disable` all work:

```ini
dotnet_diagnostic.UPNPRX001.severity = none
```

`ExcludeAssets="analyzers"` does **not** work; it has been measured three times, in three
repositories, and never suppressed anything.

<a id="upnprx001"></a>
### UPNPRX001 - a port-mapping lease outside the range IGD allows

**What it catches.** A `lease` argument to `AddPortMappingAsync`, `AddAnyPortMappingAsync` or
`PortMapper.AddPortMappingAsync`, or a `PortMappingEntry.LeaseDuration`, that is negative or
above 604 800 seconds (7 days).

**Why it matters.** IGD carries the lease as a `ui4` of seconds, ranged 0-604800 by the
standardized service template, where **zero means indefinite**. That makes the negative case
genuinely dangerous rather than merely wrong: .NET's floating-point to integer conversion
saturates, so `(uint)(-5.0)` is `0` - and asking for a lease five seconds in the past used to
ask the router for a **permanent** port forward, with no exception and no log line.

```csharp
// Reported: silently became a permanent mapping before 6.0.0.
await using var lease = await gateway.AddPortMappingAsync(
    8080, 8080, Protocol.Tcp, "my app", TimeSpan.FromSeconds(-5));
```

**How to fix it.** Pass a lease inside the range, or say what you meant:
`LeaseDurations.Maximum` for the longest IGD allows, `LeaseDurations.Indefinite` for a mapping
that never expires. Two code fixes offer exactly those - and "indefinite" is offered *only*
for a negative lease, because someone who wrote 30 days meant "a long time", not "forever".

<a id="upnprx002"></a>
### UPNPRX002 - a `UpnpClientOptions` value outside its documented range

**What it catches.** A non-positive `DescriptionTimeout`, `ActionTimeout` or
`RosterExpiryFallback`, or an `EventSubscriptionTimeout` under one second - in an object
initializer or a `with` expression.

**Why it matters.** A non-positive timeout cancels immediately, so every description fetch or
SOAP call fails before it starts. A sub-second `EventSubscriptionTimeout` composes
`TIMEOUT: Second-0`, which is malformed GENA (UDA 2.0 clause 4.1.2); a negative one composes
`Second--5`.

```csharp
// Reported: every SOAP call would fail before it started.
var options = new UpnpClientOptions { ActionTimeout = TimeSpan.Zero };
```

**How to fix it.** Use a positive duration. Note what is *not* reported: a short but positive
timeout. `ActionTimeout = TimeSpan.FromMilliseconds(100)` may be exactly right on a fast LAN,
and this rule does not second-guess it.

No code fix - there is no defensible value to substitute for your intent.

<a id="upnprx003"></a>
### UPNPRX003 - the port mapping's lease is discarded

**What it catches.** Awaiting a port-mapping call as a bare statement, or assigning it to `_`,
so the returned lease goes nowhere.

**Why it matters.** The lease owns three things: the mapping on the router, a renewal loop
that runs for the life of the process, and - for the `PortMapper` one-liner - the discovery
client and its sockets. Dropping it leaks all three and leaves an open port with nothing left
to close it. `CA2000` does not cover this: its analysis tracks object creations, not a
method's return value.

```csharp
// Reported: the mapping is never removed.
await gateway.AddPortMappingAsync(8080, 8080, Protocol.Tcp, "my app", TimeSpan.FromHours(1));
```

**How to fix it.** Hold the lease, and dispose it asynchronously - the code fix does this:

```csharp
await using var lease = await gateway.AddPortMappingAsync(
    8080, 8080, Protocol.Tcp, "my app", TimeSpan.FromHours(1));
```

`await using`, not `using`: the async path deletes the mapping from the gateway, while the
sync path only stops renewing and lets it lapse. Both are legitimate - `Dispose` is the
documented abrupt path - which is why a sync `using` is *not* reported.

<a id="upnprxgen001"></a>
### UPNPRXGEN001 - the SCPD document a generated wrapper names was not supplied

Reported by the source generator below when `[ScpdService("X.scpd.xml")]` names a document
that is not among the compiler's `AdditionalFiles`. Without it you would get no generated
members and a pile of "no such method" errors at every call site instead of the actual cause.

## Trimming and native AOT

The library declares `IsTrimmable` and `IsAotCompatible`, and CI holds it to the claim rather
than taking its word: every build publishes a sample as a **native AOT binary and runs it**.
ILC staying quiet only proves the managed side is clean; it does not prove the result starts.

That is affordable because nothing here is discovered at run time - no reflection, no
serializers, no DI scanning. XML is LINQ to XML throughout (`XDocument`/`XElement`), regexes
are `[GeneratedRegex]`, log messages are `[LoggerMessage]`. Publishing your own app is the
ordinary command:

```bash
dotnet publish -c Release -r linux-x64 --self-contained -p:PublishAot=true
```

One thing worth knowing, because its failure looks like something else entirely: **publish for
the architecture you are on.** Asking for `-r linux-x64` on an arm64 machine is a cross-compile,
and the native linker rejects flags meant for another target with an error that reads exactly
like a missing toolchain. On Linux you also want `clang` and `zlib1g-dev` installed, which is
what Microsoft documents as the prerequisite.


## Typed service wrappers from an SCPD (source generator)

Every action name, argument name and out-argument name in UPnP is a string that a device
validates by returning a SOAP fault. An SCPD document already describes all of them, so the
package generates the typed wrapper instead:

```csharp
[ScpdService("WANIPConnection1.scpd.xml")]
public sealed partial class WanIpConnection;
```

with the document handed to the compiler:

```xml
<ItemGroup>
  <AdditionalFiles Include="Scpd/WANIPConnection1.scpd.xml" />
</ItemGroup>
```

**What it emits**, over the same `IUpnpService.InvokeAsync` everything else uses: a
constructor taking the service, one `…Async` method per action with typed parameters, a nested
`record` per action that has out-arguments, and the document's own `allowedValueRange` and
`allowedValueList` checked before anything reaches the network.

```csharp
var wan = new WanIpConnection(service);
var status = await wan.GetStatusInfoAsync();          // typed result record
await wan.AddPortMappingAsync("", 8080, "TCP", 8080, "192.168.1.42", true, "my app", 3600);
```

**What it does not promise.** The wrapper describes the **document**, not the device. A
checked-in SCPD is the standardized service template, and real devices deviate from it - which
is why this library's parsers are lenient. A generated method compiling proves the *template*
declares that action; it proves nothing about the box on your network, which can still answer
with a `UpnpActionException`.

**Seeing the output.** Set `<EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>` and
look under `obj/`. Keep the path inside `obj/` - anywhere else in the project directory gets
globbed back in as compile input and every generated file is compiled twice.

**Generated members are public API with no deprecation path.** Renaming an action in the
document renames a method for everyone who called it. UPnP.Rx holds its own generated surface
to the public-API ledger for exactly that reason.


## Samples

- [`samples/Sample.PortMapper`](samples/Sample.PortMapper) - discover the gateway, print the external IP and mapping table; `--map` holds an auto-renewing mapping.
- [`samples/Sample.Browser`](samples/Sample.Browser) - discover everything on the network and dump device trees and services.
- [`samples/Sample.Eventing`](samples/Sample.Eventing) - subscribe to any evented service and watch `UpnpEvent`s live (the manual test protocol lives in the 4.0 plan).
- [`samples/Sample.Dashboard`](samples/Sample.Dashboard) - a Blazor + FluentUI dashboard: live device cards that appear and vanish as devices join and leave the network - Rx end to end. See below.

All need a real network (multicast does not work in containers).

### Running the dashboard

One command runs everything - the server hosts the WebAssembly client, so there
is nothing separate to start:

```bash
dotnet run --project samples/Sample.Dashboard
```

Then open **http://localhost:5287** (or the HTTPS URL from the console output).

How it fits together: the *server* process is the only thing touching the
network - it owns the `UpnpClient`, does the SSDP listening (which the browser
sandbox cannot), and streams `DeviceUp`/`DeviceGone` over a SignalR hub that
replays the current roster to every newly connected browser. The *client* runs
as WebAssembly in your browser: SignalR feeds a DynamicData cache, bound
through a ReactiveUI view model into FluentUI cards. Open the page from any
machine that can reach the server - phones included.

The usual multicast rules apply to the **server** process: run it on a real
host (not a container), and on Windows pause the "SSDP Discovery" service
first (`net stop SSDPSRV`, elevated - see [Troubleshooting](#troubleshooting)).
The dashboard itself repeats these hints when nothing shows up after a few
seconds of scanning.

Two pages: **Network** (live device browser with service drill-down) and
**Port mapping** (gateway status, the mapping table, add/delete with
auto-renewing leases and a live renewal-event feed). A word of caution: the
port-mapping page lets any browser that can reach the server *modify the
router*, and the sample has no authentication - treat it as a LAN tool.

## About this project - the role of AI

The work leading to UPnP.Rx goes back more than a decade - through
[HttpMachine.PCL](https://github.com/1iveowl/HttpMachine),
[SimpleHttpListener.Rx](https://github.com/1iveowl/SimpleHttpListener.Rx) and
[SSDP.UPnP.PCL](https://github.com/1iveowl/SSDP.UPnP.PCL), each hand-built and
refined over years of real-world use. 

UPnP.Rx is the first library in the
family built with AI assistance from the very first commit.

I still write code, and I review what the AI produces. For a library of this
size, resting on this much prior work, AI made the building of it much easier - but it could not have created it by itself. I set the direction, settled the design decisions, and reviewing the generated code sometimes meant
demanding fundamental changes; even those, though, were far faster to refactor
with AI in the loop. 

What did not change is the bar: the project plan, the real-life testing across various platforms, the
settled policies (time model, disposal model, Rx rules), the UDA 2.0 compliance
review and an adversarial pre-release code review are all in this repo - the
same attention to detail and focus as the siblings over the past 10 years, applied faster.

Everything that steered the work ships with the repo, on purpose: the agent
instructions ([CLAUDE.md](CLAUDE.md) / [AGENTS.md](AGENTS.md)) and the full
plan, decision record and reviews under [plan/](plan/). For transparency and for anyone who wants to contribute.

If there is one lesson from building this way, it is that **getting specs right is the key** -
the quality of what AI produces tracks the quality of the plan and the rules
set up front. This code wasn't "vibe-coded" it was managed and directed, with the AI as a tool.

## Upgrading from 4.x

One breaking change, and it is the point of the release: a device's boot identity is
no longer a bare number, because a `uint` cannot say "the device sent nothing" - and
the UPnP 1.0 installed base sends nothing.

```csharp
// 4.x - 0 meant both "the device said 0" and "the device said nothing"
if (device.BootId != previous.BootId) { /* rebooted? maybe. */ }

// 5.0 - three states, and one question with one answer
if (device.BootSignature.IndicatesRebootSince(previous.BootSignature)) { /* rebooted. */ }

device.BootSignature.BootId    // uint?   - BOOTID.UPNP.ORG, null when not sent
device.BootSignature.Nls       // string? - the UPnP 1.0 signature, when that is what it sends
device.BootSignature.IsKnown   // false   - reboots are undetectable for this device
```

Alongside it: `Announcement.MaxAge` is `TimeSpan?` (null when no usable lifetime was
announced, `TimeSpan.Zero` when the device genuinely said zero), and the presence types
moved to `UPnP.Rx.Presence` - `RosterChange`, `Announcement` and friends need that
`using`. Everything else is additive.

## Version history

| Version | Notes |
|---|---|
| 6.0.0 | **Breaking.** *Build time instead of run time.* The package now ships analyzers and a source generator - no extra reference. Three rules: **UPNPRX001** (a port-mapping lease outside IGD's 0-604800 s; a negative lease used to saturate to zero and silently ask the router for a *permanent* forward), **UPNPRX002** (a `UpnpClientOptions` duration outside its documented range), **UPNPRX003** (the lease from a port-mapping call discarded, so nothing ever removes the mapping - `CA2000` does not catch it). Most candidate rules were deleted instead of written, by making the state unrepresentable: `mx` becomes `MxSeconds` (the UDA floor is now a type invariant and the ceiling is reported by SSDP.UPnP.PCL's own SSDP001 at your call site), `EventCallbackPort` becomes `ushort`. A new SCPD source generator turns a checked-in service description plus `[ScpdService]` into typed action methods with the document's own range checks. `PortMappingEntry.LeaseDuration` and its ports become nullable, so a gateway that reports nothing stops reading as "this mapping never expires". Rides SSDP.UPnP.PCL 10.0 (send/receive message split, `MulticastMSearch`, `MxSeconds`) and SimpleHttpListener.Rx 7.6 - and fixes a bug that upgrade exposed: a USN whose entity part cannot be parsed is now delivered rather than killing the discovery stream. |
| 5.0.0 | **Breaking.** *Absence is now representable* - an optional field a device did not send is no longer reported as a value it did not send. `DiscoveredDevice.BootId` (`uint`) becomes `BootSignature`, carrying `BOOTID.UPNP.ORG`, the UPnP 1.0 `NLS` signature, or neither, so a UPnP 1.0 device that reboots is finally detected and one that announces no boot identity is never mistaken for a value of 0; `Announcement.MaxAge` becomes `TimeSpan?`. Event subscriptions now follow their device's presence: a byebye or an unannounced `BOOTID` change abandons the subscription with `SubscriptionCancelled` and a reason, rather than leaving it silently dead until a renewal fails minutes later (UDA 2.0 clause 4.1.1). `ssdp:update` is honoured, so a multi-homed device changing address is no longer mistaken for a restart. New `UpnpVersionClaims` records what a device claims about its UDA version in each of the four places the spec makes normative, keeping the provenance rather than reconciling silently. Presence types move to `UPnP.Rx.Presence`; `IUpnpService` gains `VersionClaims`; new `LocalNetwork.IPv4Addresses()`; all logging source-generated. Dashboard: version badges with a disagreement flag, restart markers, departed devices retained and grayed, a deep `ssdp:all` search. Rides SSDP.UPnP.PCL 9.1 (nullable `BOOTID` and advertisement lifetime, `NLS`, corrected `SERVER` version parsing, `IAsyncDisposable`) and SimpleHttpListener.Rx 7.4. |
| 4.2.0 | Structural release: `IUpnpClient` (mock/decorate the client), public-API ledger (PublicApiAnalyzers - surface changes now fail the build), the description cache extracted and directly unit-tested, engine/HTTP-exchange/test-helper dedups (`EngineSource`, `TimedExchange`, `TestKit`), decision ledger (`plan/DECISIONS.md`), CI trimmed-publish smoke and symbol packages on GitHub releases. No behavioral changes. |
| 4.1.0 | Device roster (`Roster()`: presence changes with replay, max-age expiry, reboot detection and lazy description self-healing), `Announcements()` + `SearchAsync()` (activity feed and solicitation), typed AV `LastChange` decoding (`UPnP.Rx.Eventing.Av`), `TryService`, trim/AOT-clean, memory audit with soak tests; dashboard: generic SCPD-driven action invocation with confirm-step, volume/mute/transport quick controls that follow the device live, and a per-device SSDP activity log. |
| 4.0.0 | GENA eventing: `service.Events()` as a shared observable with automatic renewal, SEQ-gap recovery and last-known-state replay; permanent SUBSCRIBE refusals surfaced as `SubscriptionRefused` (devices advertise eventSubURLs they don't honor - Sonos QPlay/SpeakerGroup); clause 4 compliance review; dashboard live-event watching and rescan; `Sample.Eventing`. Rides SSDP.UPnP.PCL 8.0 (lazy Rx lifecycle) and SimpleHttpListener.Rx 7.3 (packet-info local endpoints, reliable restarts). |
| 3.0.0 | First release of UPnP.Rx: discovery → description → control, IGD port mapping with auto-renewing leases, SCPD-driven argument validation, and three samples including a Blazor + FluentUI live dashboard. Versioned to reflect its lineage - it builds on years of learnings from SSDP.UPnP.PCL, SimpleHttpListener.Rx and HttpMachine.PCL rather than starting from scratch. |

## Why .NET 10?

UPnP.Rx is `net10.0`-only, like its siblings [SSDP.UPnP.PCL](https://github.com/1iveowl/SSDP.UPnP.PCL) and [SimpleHttpListener.Rx](https://github.com/1iveowl/SimpleHttpListener.Rx): modern C# records for the immutable model, `TimeProvider` throughout for testable time, and no legacy TFM baggage. If you need older targets, the underlying protocol layers remain available separately.

## License

MIT - see [LICENSE](LICENSE).

## A thank-you

[Christian Geuer-Pollmann](https://github.com/chgeuer) didn't just read this library - he rebuilt the entire control point in Elixir on OTP, tests and samples included, and then wrote up what the two designs teach each other: [The same UPnP control point in .NET Rx and Elixir OTP](https://github.com/chgeuer/external_1iveowl_UPnP.rx/blob/main/upnp/docs/rx_versus_otp.md). It walks through how a graph of streams, gates and disposables becomes a topology of supervised processes - same protocol rules, different concurrency model - and is generous about both. If you have ever wondered what this codebase would look like on the BEAM, there is no better read. Thank you, Christian!
