# UPnP.Rx — Project Plan / Starting Prompt

> **How to use this document:** paste it as the opening prompt in a fresh session inside a new, empty repository. It is self-contained: it captures the vision, the ecosystem it builds on, the house conventions to replicate, a concrete API sketch, a phased build plan, and the decisions resolved with the author (Jasper, GitHub/NuGet: `1iveowl`) in §8. Implement phase by phase with one commit per phase.

---

## 1. Vision

**UPnP.Rx is the client people actually reach for.** SSDP discovery alone stops one step short of anything useful — every consumer's next line of code after discovery is "fetch the description, call an action." UPnP.Rx closes that gap: a modern, functional, Rx-based UPnP *control point* stack for .NET 10 that goes **discover → describe → control** first (released as 3.0) and adds **eventing** in 4.0, with an **IGD port-mapping client** as the headline consumer (the single most-demanded UPnP capability in .NET — game hosting, P2P, self-hosted services — and the incumbent, Open.NAT, is unmaintained).

One-line pitch: *"Discover a device, browse its services, call its actions, watch its state — as observables and immutable records."*

```csharp
// The experience to build toward:
using var upnp = new UpnpClient(ipAddress);

using var subscription = upnp.DiscoverDevices()               // SSDP under the hood
    .SelectMany(d => d.GetDescriptionAsync())                 // lazy DDD + SCPD fetch, cached
    .Where(d => d.HasService("WANIPConnection"))
    .SelectMany(async gateway =>                              // async work lives IN the pipeline…
    {
        var wan = gateway.Service("WANIPConnection");
        return await wan.InvokeAsync("GetExternalIPAddress");
    })
    .Subscribe(result =>                                      // …Subscribe is a sync side effect only
        Console.WriteLine(result["NewExternalIPAddress"]));   //    (Rx rule 1 — never Subscribe(async …))

// And the flagship convenience API on top:
var mapping = await PortMapper.AddPortMappingAsync(externalPort: 8080, internalPort: 8080,
    Protocol.Tcp, description: "my app", lease: TimeSpan.FromHours(1));
```

## 2. Landscape and audience (verified against nuget.org, July 2026)

**The niche is real.** The .NET UPnP landscape is either dead, partial, or framework-locked:

| Incumbent | Downloads | Last release | Covers | Verdict |
|---|---|---|---|---|
| Mono.Nat | 1.5M | Oct 2022 | Port mapping only (IGD + NAT-PMP) | Healthiest incumbent; dormant ~3½ years, callback API, no description/control |
| Open.NAT | 633K | Jul 2016 | Port mapping only | Dead a decade, yet still ~140 downloads/day — demand with no maintainer |
| Rssdp | 292K | Jan 2026 (active) | SSDP discovery/publishing only | Stops exactly where UPnP.Rx begins: hands the caller a LOCATION URL |
| Waher.Networking.UPnP | 155K | Apr 2026 | Discovery + some description/control | Component of the monolithic Waher IoT Gateway framework; netstandard2.1, pre-modern API style |
| Intel-derived `UPnP` pkg, Windows COM/UWP APIs | — | ancient / Windows-only | — | Not contenders |

Telling signal: Jellyfin — the highest-profile .NET UPnP consumer — vendored its own SSDP fork rather than depend on any library.

**What no one else offers (the differentiation):** the full discover → describe → control chain in one standalone library; modern .NET 10-only (trimming/AOT-friendly); an Rx + immutable-records API (nobody models device presence as observable streams); spec-audited UDA 2.0 behavior; near-zero dependencies; and single-author ownership of every layer (HttpMachine → SimpleHttpListener.Rx → SSDP.UPnP.PCL → UPnP.Rx) — no impedance mismatches, one design language, one maintenance policy.

**Audience, in order of proven demand:**
1. **Port-forwarding consumers** — P2P apps, torrent clients, game-server hosting, self-hosted tools. 2.1M combined Mono.Nat/Open.NAT downloads prove sustained demand against stale supply. *Caveat:* part of that historical audience is Unity games, which cannot consume net10.0 — the reachable slice is modern-.NET servers, MonoGame-class games and self-hosted apps.
2. **Media/DLNA tooling builders** — cast-to-TV, media-server browsers, Jellyfin-adjacent utilities; this is where describe + control matters and only the Waher monolith competes.
3. **Network/IoT tooling on modern .NET** — device inventory/scanners, home-automation bridges, camera/NAS/printer discovery, Pi-class .NET 10 devices.
4. **Rx-first developers** — small but loyal, and unserved.

**Sober framing:** UPnP is not a growth protocol (new IoT standardizes on mDNS/Matter); this niche is about interoperating with the enormous *installed base* — practically every router, TV, receiver and NAS. Expect steady niche adoption, not fireworks, and expect **the port mapper to be the door** most users walk in through, discovering the general client afterward. The phasing in this plan (port mapping as the v1 flagship) exists because of this. Rssdp's active maintenance competes with SSDP.UPnP.PCL's slice, not with UPnP.Rx's.

## 3. The ecosystem this builds on

All by the same author, all net10.0, all on nuget.org, all MIT:

| Package | Role | Notes |
|---|---|---|
| `SSDP.UPnP.PCL` ≥ 7.0.2 | SSDP discovery (UDA 2.0 clause 1) | The foundation. Exposes `ControlPoint` with `MSearchResponseObservable()` / `NotifyObservable()` (shared, parse-once streams), immutable records (`MSearchRequest/Response`, `Notify`, `ST`, `USN`), pure `SsdpMessageParser`/`DatagramComposer`, TCPPORT (TCP search responses), and a `HotStart` seam for sharing one socket stream between services. Handles the Linux/macOS multicast-bind semantics internally — UPnP.Rx never touches multicast sockets. |
| `SimpleHttpListener.Rx` ≥ 7.0.1 | Rx HTTP listener (TCP/UDP), keep-alive aware | Use its `TcpListener.ToHttpListenerObservable(...)` for the eventing callback server in 4.0, via the SSDP `HotStart` seam if sharing a listener. `HttpRequestResponse` record is the shared message currency. |
| `HttpMachine.PCL` ≥ 6.0.1 | Span-based HTTP parser | Transitive; not referenced directly. |

UPnP.Rx sits **above** `SSDP.UPnP.PCL` and never reimplements discovery. Description documents are fetched with plain `HttpClient` (injectable). SOAP control is plain `HttpClient` POST. Only eventing (4.0) needs an inbound HTTP listener.

**Do not modify SSDP.UPnP.PCL from this project.** If a gap is found there, note it as a proposed upstream issue.

## 4. Scope

### v1 (this plan's deliverable)
- **Description (UDA 2.0 clause 2)** — fetch and parse the device description document (DDD) and each service's SCPD into immutable records; resolve relative URLs against `LOCATION` (UDA 2.0 dropped `URLBase`); honor `configId`.
- **Control (UDA 2.0 clause 3)** — compose/parse SOAP 1.1 action calls (`POST` to `controlURL`, `SOAPACTION: "urn:...:service:X:1#Action"` header), typed argument marshalling per SCPD, `UPnPError` fault parsing (codes 401/402/501/6xx).
- **The fluent client** — `UpnpClient` tying discovery → lazy cached description → service invocation, deduplicating discovered devices by USN/BOOTID.
- **IGD port mapping** — `InternetGatewayDevice:1/:2` + `WANIPConnection:1/:2` (fallback `WANPPPConnection:1`): `AddPortMapping`, `AddAnyPortMapping` (IGD:2), `DeletePortMapping`, `GetExternalIPAddress`, `GetGenericPortMappingEntry` enumeration, auto-renewing `PortMappingLease` handle (decision 3).

### 4.0 (design for it, don't build yet)
- **Eventing (UDA 2.0 clause 4, GENA)** — control-point side only: `SUBSCRIBE`/`UNSUBSCRIBE`/renewal (`NT: upnp:event`, `CALLBACK`, `TIMEOUT: Second-…`, `SID` lifecycle), callback listener via SimpleHttpListener.Rx, `NOTIFY`/`upnp:propchange` property-set XML parsing, event-key (`SEQ`) gap detection, exposed as `service.Events() → IObservable<PropertyChange>` with automatic renewal driven by `TimeProvider`.

### Explicitly out of scope
Device-side hosting (serving descriptions, accepting subscriptions), presentation (clause 5), DeviceProtection, IPv6, UPnP certification. A future `UPnP.Rx.Device` could add hosting; do not design for it now beyond keeping parsers/composers symmetric (pure functions compose as easily as they parse).

## 5. Architecture and shape

- **One repository, one package `UPnP.Rx` for v1**, with namespaces `UPnP.Rx` (client), `UPnP.Rx.Model` (records), `UPnP.Rx.Parsing` (pure functions), `UPnP.Rx.PortMapping` (IGD). Splitting into sub-packages (`UPnP.Description.Rx`, …) is premature until a consumer demands a smaller closure — revisit at v2 when eventing lands.
- **Functional house style (non-negotiable, matches SSDP.UPnP.PCL):**
  - All data types are immutable `record`s with `init` accessors; derived state via `with`.
  - Parsing is pure and total: `static ParseResult<T> Parse(...)` — no constructor side effects, no exceptions for bad input. Reuse the `ParseResult<T>` shape from SSDP.UPnP.PCL (copy the ~40-line record; do not couple packages for it — or see open question 5).
  - Composition (SOAP envelopes, SUBSCRIBE requests) is pure: input records → `string`/`byte[]`, no ambient state (no `DateTime.Now` inside composers — timestamps are inputs).
  - Side effects live only at the edges: `HttpClient` calls, socket listeners, subscription state. Those edge classes take `TimeProvider` (default `TimeProvider.System`) and `CancellationToken ct = default` on every public async method. Time handling follows **"The time model"** below — read it before writing any temporal code.
  - **Leniency policy, stated in XML docs:** be strict in what you send, lenient in what you accept. Real-world UPnP devices ship malformed XML, wrong namespaces, and bogus URLs; parsers leave unparsable optional fields unset and only fail when a document identifies nothing. (This policy proved essential in SSDP.UPnP.PCL.)
  - Rx pipelines must never die from one bad message: per-item try/catch with `ILogger` (Microsoft.Extensions.Logging.Abstractions), errors reserved for source death.
- **XML handling:** `System.Xml.Linq` (XDocument) — no external XML deps. Namespace-tolerant lookups (match local names when devices botch namespaces).
- **Dependencies (v1):** `SSDP.UPnP.PCL`, `Microsoft.Extensions.Logging.Abstractions`, `System.Reactive` — nothing else. Check latest versions on nuget.org at project start.

### The time model (crisp rules — this is settled policy, not an open question)

Background: Rx and the BCL have two *unrelated* time abstractions. Rx operators consult `IScheduler` (default: wall clock); the BCL virtualizes time via `TimeProvider`. **System.Reactive 7.0.0 contains no `TimeProvider` integration whatsoever** (verified against the assembly). Left undisciplined, a codebase ends up with two clocks that tests cannot advance together — the classic source of hanging and flaky time tests. Hence:

1. **`TimeProvider` is the production clock, not a test utility.** Every component that needs "now" or "later" exposes `TimeProvider` (default `TimeProvider.System`). Production runs the real clock; tests inject `FakeTimeProvider` — the *same* code path either way. Banned everywhere, including tests: `DateTime.Now/UtcNow`, `DateTimeOffset.Now/UtcNow`, `Stopwatch.StartNew`, `Task.Delay(x)` without a provider, `new CancellationTokenSource(timeout)` without a provider. (Enforce with `Microsoft.CodeAnalysis.BannedApiAnalyzers` + a `BannedSymbols.txt` — cheap and mechanical.)
2. **Rx composes; TimeProvider times.** Pipelines default to logically timeless operators (`Where`/`Select`/`SelectMany`/`Merge`/`Scan`/`DistinctUntilChanged`/`Publish`/`RefCount`). Temporal behavior lives at the async edges via TimeProvider-aware primitives: `Task.Delay(ts, timeProvider, ct)`, `new CancellationTokenSource(timeout, timeProvider)` for timeouts (e.g. SOAP calls), `PeriodicTimer(period, timeProvider)` for loops (e.g. lease renewal), `timeProvider.GetUtcNow()/GetTimestamp()` for stamps.
3. **Time-based Rx operators are allowed — with one iron rule.** `Buffer(TimeSpan)`, `Throttle`, `Sample`, `Timeout`, `Delay`, `Window` are legitimately the best expression for some of this domain (batching a discovery response burst, taming an eventing storm). When used, the operator **must receive an explicit `IScheduler`** — never the implicit `Scheduler.Default` — and that scheduler **must be a `TimeProviderScheduler` wrapping the component's own `TimeProvider` instance**. One clock, no matter which door time enters through. Review heuristic: *any Rx operator call with a `TimeSpan` argument must also have a scheduler argument.*
4. **`TimeProviderScheduler` is ours to build** (Rx doesn't ship one): a small `LocalScheduler` over `timeProvider.GetUtcNow()` + `timeProvider.CreateTimer()` (~40 lines), living in the internals, unit-tested by driving a `Buffer`/`Timeout` pipeline with `FakeTimeProvider.Advance()`. Build it in the first phase that uses a time-based operator — not speculatively.
5. **One clock per test.** A test creates exactly one `FakeTimeProvider` and injects it everywhere (components and, via `TimeProviderScheduler`, any Rx operators). Rx's `TestScheduler` (`Microsoft.Reactive.Testing`) is **banned** — it is a second virtual clock and its presence in a test is a design smell.

What goes wrong without rule 3, concretely: `.Buffer(TimeSpan.FromSeconds(1))` with no scheduler runs on wall-clock `Scheduler.Default`; under `FakeTimeProvider` the test advances fake time, the buffer never closes (or closes on real time), and the test hangs, flakes, or silently measures the wrong thing. The failure is invisible in production and only bites in tests — the worst kind.

### The disposal model (crisp rules — settled policy, companion to the time model)

Background: Rx subscriptions are synchronously disposable by contract, and **System.Reactive 7.0.0 has no `IAsyncDisposable` integration** (verified against the assembly; AsyncRx.NET is experimental preview — do not use). But UPnP teardown is inherently *asynchronous protocol work*: deleting a port mapping, sending `UNSUBSCRIBE`, saying byebye. Hence the house rule extends to three parts: **Rx composes; TimeProvider times; IAsyncDisposable retires.**

1. **Edge classes that owe the network a goodbye implement `IAsyncDisposable`.** `DisposeAsync` performs the graceful protocol exit, then releases resources. Also implement plain `IDisposable` as the *abrupt* variant (release only, no network goodbye), with XML docs stating exactly that. Applies to: `UpnpClient`, the auto-renewing port-mapping handle (`await using var mapping = …` → `DisposeAsync` stops renewal and calls `DeletePortMapping`), and in 4.0 the eventing subscription (`DisposeAsync` sends `UNSUBSCRIBE`).
2. **The `FinallyAsync` pattern is banned** (Materialize → async SelectMany → Dematerialize, as seen in WebsocketClientLite's `ObservableEx` and in SSDP.UPnP.PCL v6, where it was removed during the 7.0 review). Its flaw: it fires on `OnCompleted`/`OnError` but **never on unsubscribe** — and disposing the subscription is how consumers normally leave a hot, long-lived stream, so the cleanup silently never runs. It also swallows the original error if the cleanup task throws during the `OnError` path. Cleanup that must run when the consumer leaves belongs either on the owning object's `DisposeAsync` (rule 1) or in rule 3's pattern.
3. **Per-subscription async teardown, when genuinely needed, uses `Observable.Create` with the async subscribe overload**: `Observable.Create<T>(async (observer, ct) => { try { … } finally { await CleanupAsync(); } })` — the token fires on unsubscribe as well, so the `finally` covers completion, error *and* disposal (SimpleHttpListener.Rx v7's UDP listener is the reference implementation of this shape). If the resource is an `IAsyncDisposable`, wrap the pattern once as a small internal `ObservableEx.UsingAsync(...)` helper — that is the helper worth owning, not `FinallyAsync`.
4. **Fire-and-forget async work inside a sync `Dispose` is forbidden** — it races process shutdown and hides failures. If teardown matters, it is `DisposeAsync`'s job; if `Dispose` is called instead, the abrupt path is taken by design.

Upstream note (do not implement from this repo): SSDP.UPnP.PCL's `Device` currently documents "call `ByeByeAsync` before `Dispose`" because `Dispose` is sync — a 7.1 candidate is `IAsyncDisposable` with `DisposeAsync` = byebye + cleanup, turning the documented footgun into `await using`.

### Rx and functional rules (settled policy — the review checklist; upstream-verified idioms marked ✓)

1. **Async work never rides `Subscribe`.** `Subscribe(async x => …)` compiles to `async void`: exceptions bypass the pipeline (and can kill the process), completion is unobservable, there is no cancellation. Async steps live *in* the pipeline — `SelectMany` (unordered; fine for discovery), `Select(…).Concat()` (ordered), `Observable.FromAsync` — and `Subscribe` handlers are synchronous side effects only. The §1 vision snippet models this. **Prefer the token-aware forms** — `SelectMany(x => Observable.FromAsync(ct => …))` (or the `(item, ct)` `SelectMany` overload) — over a plain `async` lambda closing over an ambient token: the `FromAsync` token fires on unsubscription, so in-flight work is cancelled the moment the pipeline stops caring (e.g. when `FirstAsync` has its winner), and per-item failure handling becomes a declarative `Catch` instead of try/catch-to-null (see `PortMapper.DiscoverGatewayAsync`).
2. **No blocking in or around pipelines.** `.Wait()`, `.Result`, `GetAwaiter().GetResult()`, blocking `First()`/`Last()` — never, anywhere. Bridge to async with `await stream.FirstAsync()`, `ToTask(ct)`, `ToAsyncEnumerable()`.
3. **Temperature is API contract.** Every public observable member's XML docs state hot vs cold and when work starts. House shape (✓ both upstream libs): expensive shared sources are built once and shared via `Publish().RefCount()` — each message parsed exactly once regardless of subscriber count; lazy/per-subscriber work wraps in `Observable.Defer`/`Create` so nothing runs before subscription. Note `RefCount` semantics: dropping to zero subscribers stops the source; resubscribing restarts it (✓ SHL listeners restart by design — document the same for our streams).
4. **No Subjects in public APIs.** A Subject is at most an internal edge tool; public streams come from composition or `Observable.Create`'s async-subscribe overload (✓ the SHL UDP listener is the reference: linked CTS over the subscription token, receive loop, `OperationCanceledException` → `OnCompleted`).
5. **The Rx grammar is law.** `OnNext* (OnError|OnCompleted)?`, notifications serialized, nothing after terminal. Our `Observable.Create` bodies emit from a single logical loop; multi-interface fan-in goes through `Merge`, which preserves the grammar.
6. **Per-item failure is data; stream failure is death.** Already policy (§5); the Rx corollary: streams surface degraded items as typed values (or drop + `ILogger`) so consumers can `Retry`/`Catch` at *their* edge — never `OnError` for one bad device.
7. **Deterministic subscription lifetime.** Edge classes own a `CompositeDisposable`; every internal subscription joins it and dies with the owner (disposal model). No fire-and-forget subscriptions, no orphaned timers.
8. **Purity discipline.** Pure layers (`UPnP.Rx.Parsing`): static, total, exception-free, `ParseResult<T>` out, and no I/O, clock, randomness, or logging — those are edge inputs. Model layer: `sealed record`, `init` (+ `required` where identity demands), collection properties defaulting to empty (`FrozenDictionary<,>.Empty` ✓ upstream idiom, collection expressions `[]`), never null collections.
9. **Exceptions are edge vocabulary.** Typed edge exceptions only — `UpnpException` (contract misuse: unstarted client, unknown service), `UpnpActionException : UpnpException` carrying `UpnpError` (SOAP fault). Parse outcomes are values, never exceptions (rule 8).
10. **Library-async etiquette.** `ConfigureAwait(false)` on every `await` (enforced: CA2007 as error via `.editorconfig`); `CancellationToken ct = default` on every public async method; no `async void` ever; `IAsyncEnumerable<T>` endpoints take `[EnumeratorCancellation]`. (✓ SimpleHttpListener.Rx complies today; SSDP.UPnP.PCL has 0 of 26 awaits suffixed — noted in §9 as an upstream issue candidate, not fixed from here.)

### API sketch (v1)

```csharp
// Model (records, excerpt)
public sealed record DeviceDescription { SpecVersion, ConfigId, Udn, DeviceType, FriendlyName,
    Manufacturer, ModelName, ModelNumber, SerialNumber, PresentationUrl,
    IReadOnlyList<ServiceDescription> Services, IReadOnlyList<DeviceDescription> EmbeddedDevices,
    Uri Location /* absolute base used for URL resolution */ }
public sealed record ServiceDescription { ServiceType, ServiceId, Uri ScpdUrl, Uri ControlUrl, Uri EventSubUrl }
public sealed record Scpd { IReadOnlyList<ActionDescription> Actions, IReadOnlyList<StateVariable> StateVariables }
public sealed record ActionDescription { Name, IReadOnlyList<ArgumentDescription> Arguments }
public sealed record ActionResult { IReadOnlyDictionary<string,string> Out; string? this[string name] }
public sealed record UpnpError { int Code, string Description }   // SOAP fault payload

// Pure parsing (UPnP.Rx.Parsing)
public static class DescriptionParser {
    ParseResult<DeviceDescription> ParseDeviceDescription(string xml, Uri location);
    ParseResult<Scpd> ParseScpd(string xml); }
public static class SoapComposer { string ComposeActionRequest(serviceType, action, args); }
public static class SoapParser { ParseResult<ActionResult> ParseActionResponse(...); ParseResult<UpnpError> ParseFault(...); }

// Edge (UPnP.Rx)
public sealed record UpnpClientOptions {                // decision 6
    ST DefaultSearchTarget { get; init; }               // = SearchTargets.RootDevice
    TimeSpan DefaultMx { get; init; }                   // = 3 s
}
public sealed class UpnpClient : IAsyncDisposable, IDisposable {   // see "The disposal model"
    UpnpClient(params IPAddress[] addresses);           // owns an SSDP ControlPoint, default options
    UpnpClient(UpnpClientOptions options, params IPAddress[] addresses);
    UpnpClient(IControlPoint controlPoint, HttpClient?, UpnpClientOptions?) // advanced: bring your own
    IObservable<DiscoveredDevice> DiscoverDevices(ST? searchTarget = null, TimeSpan? mx = null);
    // null falls back to options; explicit argument wins
    IObservable<DiscoveredDevice> DeviceLost();         // byebye / cache expiry
}
public sealed class DiscoveredDevice {   // discovery envelope + lazy description
    USN Usn; Uri Location; Server Server; uint BootId;
    Task<DescribedDevice> GetDescriptionAsync(CancellationToken ct = default);  // cached by Location+ConfigId
}
public sealed class DescribedDevice {
    DeviceDescription Description;
    UpnpService Service(string serviceTypeOrId);        // throws UpnpException if absent
    bool HasService(string serviceTypeOrId);
}
public sealed class UpnpService {
    Task<Scpd> GetScpdAsync(ct);
    Task<ActionResult> InvokeAsync(string action, IReadOnlyDictionary<string,string>? args = null, ct);
    // throws UpnpActionException (carries UpnpError) on SOAP fault
}

// Port mapping (UPnP.Rx.PortMapping) — the flagship
public static class PortMapper {
    Task<InternetGateway?> DiscoverGatewayAsync(TimeSpan timeout, ct);  }
public sealed class InternetGateway {
    Task<IPAddress> GetExternalIPAddressAsync(ct);
    Task<PortMappingLease> AddPortMappingAsync(external, internalPort, Protocol, description, lease, ct);
    Task DeletePortMappingAsync(external, Protocol, ct);
    IAsyncEnumerable<PortMapping> GetPortMappingsAsync(ct); }
public sealed class PortMappingLease : IAsyncDisposable, IDisposable {  // decision 3 + disposal model
    PortMapping Mapping;                            // as granted (AddAnyPortMapping may shift the external port)
    IObservable<PortMappingEvent> Events;           // Renewed | RenewalFailed (retrying) | Expired
    // DisposeAsync = stop renewal + DeletePortMapping; Dispose = stop renewal only (lease expires on router)
}
```

## 6. House conventions to replicate (proven in SSDP.UPnP.PCL 7.x)

- **Repo layout:** `src/UPnP.Rx/`, `tests/UPnP.Rx.Tests/`, `samples/` (at least `Sample.PortMapper` and `Sample.Browser` — discover + dump descriptions), `UPnP.Rx.slnx` at root, `assets/1iveowl-logo.png` as package icon.
- **`Directory.Build.props`:** `net10.0`, `LangVersion latest`, `Nullable enable`, `ImplicitUsings enable`, `TreatWarningsAsErrors true`, authors/license (MIT)/repo URL metadata, `ContinuousIntegrationBuild` when `CI=true`. Plus `.editorconfig` (file-scoped namespaces, expression-bodied members, pattern matching preferred) and `global.json` (SDK `10.0.100`, `rollForward: latestFeature`).
- **Library csproj:** `GenerateDocumentationFile` — **XML docs on every public member, enforced** (docs ship in the nupkg, so doc-only changes still warrant a patch release). `IncludeSymbols` + snupkg, `PackageReadmeFile`, icon, tags. `InternalsVisibleTo` for the test project.
- **Tests:** xUnit **v3** + `Microsoft.NET.Test.Sdk` + `xunit.runner.visualstudio` + `Microsoft.Extensions.TimeProvider.Testing` (check latest versions). Deterministic-first: pure parsers get input→record assertions with real-world captured XML fixtures (grab actual DDD/SCPD samples from common devices — include at least one *malformed* real-world fixture per parser to pin leniency). Edge classes get fake `HttpMessageHandler` for HttpClient, `FakeTimeProvider` for renewals/timeouts (one fake clock per test — see the time model, rule 5), Rx `Subject`s via the bring-your-own-ControlPoint constructor. Loopback TCP integration tests are fine; **multicast does not work in devcontainers** — never depend on it in tests (SSDP is faked at the `IControlPoint` seam anyway).
- **CI (GitHub Actions):** build `-warnaserror` → test → pack on push/PR; on `v*` tags publish via **NuGet Trusted Publishing** — job `permissions: id-token: write`, `NuGet/login@v1` with `user: 1iveowl`, push with the short-lived key. Requires a Trusted Publishing policy on nuget.org for the new repo before the first tag.
- **Release discipline:** `releases/x.y.z` branches, frozen once tagged `vx.y.z`; **any** change after tagging — even docs — means a new patch version on a new branch; `main` always holds the latest release; brief commit messages, one commit per phase.
- **Spec discipline:** the UDA 2.0 PDF is fetchable from `https://upnp.org/specs/arch/UPnP-arch-DeviceArchitecture-v2.0.pdf` (extract text with `pdftotext`). Before declaring clause 2/3 done, run a compliance review against the spec text with findings written to a plan document for author review — this process caught nine mandatory-behavior violations in the SSDP library; assume it will catch some here too.

## 7. Phased build plan (one commit per phase, build + tests green at each)

| Phase | Deliverable |
|---|---|
| 0 | Repo infrastructure: props, editorconfig, slnx, global.json, CI skeleton, empty test project |
| 1 | Model records + `ParseResult<T>`; `DescriptionParser` (DDD) with real-device fixtures incl. malformed ones; relative-URL resolution rules |
| 2 | `ScpdParser`; `SoapComposer`/`SoapParser` incl. `UPnPError` faults — all pure, fixture-tested |
| 3 | Edge: `UpnpClient`/`DiscoveredDevice`/`DescribedDevice`/`UpnpService` — discovery wiring over `IControlPoint`, lazy cached description fetch (`HttpClient` injectable), `InvokeAsync` with `CancellationTokenSource(timeout, TimeProvider)`-based timeouts; `TimeProviderScheduler` lands here if any time-based Rx operator is introduced (time-model rules 3–4) |
| 4 | `UPnP.Rx.PortMapping`: gateway discovery (`ST` for IGD:2 then IGD:1), WANIPConnection/WANPPPConnection resolution, the port-mapping API incl. the auto-renewing `PortMappingLease` (decision 3), `Sample.PortMapper` |
| 5 | Clause 2/3 spec-compliance review (written to plan folder, reviewed by author) + fixes |
| 6 | Packaging metadata, README (mirror SSDP.UPnP.PCL's structure: overview, install, examples, behavior notes, "Why .NET 10?" note), samples polish, CI publish job |
| 7 | v3.0.0 release: `releases/3.0.0` branch, tag, Trusted Publishing |

Real-hardware smoke test (an actual router for IGD, a real media device for description parsing) is a manual author step before tagging — containers can't do multicast.

## 8. Decisions (open questions resolved with the author, 2026-07-24)

1. **Package/repo name: `UPnP.Rx`.** The `.PCL` suffix remains a legacy artifact of the SSDP package only.
2. **Versioning: first release is 3.0.0** (revised twice 2026-07-24; originally 1.0.0, then 2.0.0). The library is not a from-scratch v1 — it builds directly on years of learnings from the upstream siblings (SSDP.UPnP.PCL, SimpleHttpListener.Rx, HttpMachine.PCL), and the version says so. Still no alignment with the SSDP library's 7.x line.
3. **IGD lease renewal: auto-renew.** `AddPortMappingAsync` returns a `PortMappingLease` handle whose renewal loop runs on `PeriodicTimer(period, timeProvider)` (time model rule 2). The handle follows the disposal model: `DisposeAsync` stops renewal and deletes the mapping; sync `Dispose` stops renewal only — safe by design, because the finite lease then simply expires on the router. This synergy is deliberate: auto-renew + finite leases is the default posture; an infinite lease (0) is supported but documented as opting out of both protections. Renewal outcomes surface as `IObservable<PortMappingEvent>` on the handle — a failed renewal is `OnNext(RenewalFailed)` + retry, never `OnError` (pipelines-never-die rule); terminal only when the lease is genuinely unrecoverable (mapping gone and re-add refused).
4. **`UpnpClient` device cache: raw discovery streams in v1.** The live roster (`IObservable<IReadOnlyList<DiscoveredDevice>>` with alive/byebye/max-age bookkeeping) is deferred to v1.1 — expiry bookkeeping is where subtle bugs live.
5. **`ParseResult<T>`: copied into UPnP.Rx.** Zero package coupling; no shared functional package.
6. **Search target: the library consumer chooses.** Configured via `UpnpClientOptions.DefaultSearchTarget`, overridable per call through the `searchTarget` parameter; the out-of-box value is `upnp:rootdevice` (one response per device; the description enumerates the rest). A `SearchTargets` helper (`RootDevice`, `All`, `DeviceType(...)`, `ServiceType(...)`) saves callers from hand-building URNs.
7. **Dependency addition (author-approved 2026-07-24): `SimpleHttpListener.Rx` as a direct reference, pinned 7.2.0.** The 4.0 eventing listener consumes it directly (`ToHttpListenerObservable`, `HttpSender`), so a transitive-only reference was a smell - and 7.2.0 carries the packet-information `LocalEndPoint` fix (upstream issue candidate #3 in §9, fixed by the author in the upstream repo), so the direct pin also floors consumers onto the corrected behavior. Amends the §5 v1 dependency lock. The wildcard-address defense in `LocalRoute` stays regardless (belt and braces, and correct for older runtime graphs).

8. **ReactiveUI lifecycle in the Blazor samples: ctor-composed pipelines + explicit disposal, deliberately (2026-07-25, review RUI-6).** `IActivatableViewModel`/`WhenActivated` is ReactiveUI canon for XAML, but its Blazor story is weaker and adds ceremony; the samples pair transient view models with `ReactiveInjectableComponentBase`, compose every pipeline in the constructor into a `CompositeDisposable`, and dispose explicitly in the page (the base class cannot dispose the injected VM - commented at both sites). Alongside (review RUI-7): settable view-model properties are source-generated (`ReactiveUI.SourceGenerators` `[Reactive]`); OAPHs and commands stay hand-written - command output schedulers must be explicit on WASM (Rx 7 defect), and the OAPH pipelines are the sample's teaching surface.


## 9. Upstream verification notes (audited 2026-07-24, against cloned repos + published nupkgs)

**Confirmed as the plan claims:** the `IControlPoint` seam (`Start(ct)` / `HotStart(IObservable<HttpRequestResponse>)` / `NotifyObservable()` / `MSearchResponseObservable()` / `SendMSearchAsync`); `ParseResult<T>` (42 lines, `Success`/`Failure`/`Match`, `MemberNotNullWhen` annotations — the copy target); parse-once shared streams via `Publish().RefCount()` at both layers; the `Observable.Create` async-subscribe UDP pattern (disposal model rule 3's reference); `Task.Delay(…, TimeProvider, ct)` in `SendMSearchAsync`; house `Directory.Build.props`/`global.json`/csproj packaging shape (icon + README packed from repo root); the exact Trusted Publishing CI job (copy for Phase 6); Subject-driven `HotStart` test seam with message-builder helpers; README section structure. System.Reactive 7.0.0: packaging/trimming-era release, `lib/net8.0` asset, **no `TimeProvider`, no `IAsyncDisposable`** integration (basis of the time + disposal models). SSDP.UPnP.PCL 7.0.2 pins `System.Reactive 7.0.0`, `SimpleHttpListener.Rx 7.0.1` (7.1.0 now on nuget), `Logging.Abstractions 10.0.0` (10.0.10 now).

**Implementation deltas the audit surfaced (fold into the phases):**
- **Phase 3 — `CPFN` is required on multicast M-SEARCH** (UDA 2.0; `MSearchRequest.CPFN` is nullable upstream): `UpnpClient` sends a default (`"UPnP.Rx/{version}"`), overridable via `UpnpClientOptions`.
- **Phase 3 — multi-interface fan-out is ours:** `SendMSearchAsync(mSearch, ipAddress)` is per-interface; `UpnpClient.DiscoverDevices()` loops its addresses. `MSearchRequest.SendCount` (default 2, UDA §1.3.2 repeat-send) already handled upstream.
- **Phase 3 — start-once lifecycle:** `ControlPoint.Start(ct)` can run once; observables throw `SSDPException` before start. `UpnpClient` owns an internal linked CTS (cancelled in both dispose paths) and starts the control point lazily on first subscription (`Observable.Defer`), keeping construction side-effect-free.
- **Phase 3 — deliberate divergence:** upstream exposes `TimeProvider` as a *mutable* property; UPnP.Rx carries it as `init`-only state in `UpnpClientOptions` (a settable clock is mutable ambient state — contra the FP house style). Document the divergence.
- **Phase 3/4 — leniency flags ride the records:** `MSearchResponse`/`Notify` carry `HasParsingError` and `IsUuidUpnp2Compliant`; surface them on `DiscoveredDevice` rather than dropping degraded-but-usable responses (Rx rule 6).
- **Phase 4 — `SearchTargets` is thin sugar over `STType`:** upstream `ST` already models `RootDeviceSearch`/`DeviceTypeSearch`/`ServiceTypeSearch`/…; the helper just names the common cases, no URN string-building anywhere.
- **Phase 6 — CI detail:** run `dotnet test` against the test csproj, not the slnx, once samples join the solution (sibling repo does this for the same reason).

**Design calls made during implementation (recorded post-hoc, Phase 4):**
- **`PortMapper.DiscoverGateways(client)` is public: observable core, Task sugar.** The scalar `DiscoverGatewayAsync` is the right front door for the non-Rx majority (a single-valued question gets a `Task`), but the gateway *stream* it collapses from must be public too — `InternetGateway`'s constructor is internal, so without this the Rx-first audience (§2 #4) couldn't build it. `DiscoverGatewayAsync(client, …)` = `DiscoverGateways(client).FirstAsync()` + timeout. The stream deduplicates by device identity (an IGD:2 device answers the IGD:1 search too) and only exists for caller-owned clients — an observable owning its client has no sound disposal semantics across N subscribers, so the self-contained overload stays Task-only.
- **The mapping record is `PortMappingEntry`, not `PortMapping` as sketched in §5.** A type named identically to its containing namespace (`UPnP.Rx.PortMapping`) is unresolvable in consumer code that also imports `UPnP.Rx` — the namespace wins name lookup (CS0118). Caught by our own tests hitting the collision first.
- **WAN service priority includes `WANPPPConnection:2`.** §4 listed only `WANPPPConnection:1` as fallback, but the Orange Livebox fixture proves `:2` ships in real devices. Priority order: `WANIPConnection:2`, `:1`, `WANPPPConnection:2`, `:1` — IP over PPP, higher version first.
- **Description cache key includes BOOTID** (found on real hardware, 2026-07-24): the sketch's "cached by Location+ConfigId" assumed UDA 2.0's configId semantics, but the UPnP 1.0 installed base never sends CONFIGID — making the first read immortal for the client's lifetime. A Sonos served a sparse description mid-boot and stayed nameless until server restart. Key is now `LOCATION#CONFIGID#BOOTID`, so a reboot re-reads the device. Residual gap (device sends neither header): TTL from SSDP `CACHE-CONTROL: max-age` — v1.1 roster territory.

**Upstream issue candidates (note, do not fix from this repo — §3 rule):**
1. `SSDP.UPnP.PCL`: no `ConfigureAwait(false)` anywhere (0 of 26 awaits; SimpleHttpListener.Rx applies it consistently) — a UI-app consumer can deadlock/hop contexts unnecessarily.
2. `SSDP.UPnP.PCL`: `Device` sync-`Dispose` + documented "call `ByeByeAsync` first" — `IAsyncDisposable` candidate for 7.1 (already noted in the disposal model).
3. `SSDP.UPnP.PCL`/`SimpleHttpListener.Rx`: `LocalIpEndPoint` on received SSDP messages is the socket's *bound* endpoint — on macOS/Linux the multicast socket binds the wildcard address (`ControlPoint.CreateInterface`), so every envelope reports `0.0.0.0:1900` instead of the receiving interface (found 2026-07-24 when it became `CALLBACK: <http://0.0.0.0:…>` and devices refused SUBSCRIBE with 412). Correct fix upstream: `ReceiveMessageFromAsync` + `SocketFlags`/packet-info to report the true destination interface. UPnP.Rx defends locally: wildcard is normalized to "unknown" at the discovery boundary and a routing-table lookup answers instead (`LocalRoute`).
4. `SSDP.UPnP.PCL`/`SimpleHttpListener.Rx`: the shared RefCounted message streams do not survive dispose-then-immediately-resubscribe (observed 2026-07-25, dashboard rescan): when the subscriber count touches zero the socket teardown races the restart, and the canceled `TcpListener.AcceptTcpClientAsync` surfaces as `SocketException (89)` through `OnError` on the *new* subscription (SHL's accept loop treats only `OperationCanceledException` as a normal stop). Two upstream angles: SHL's accept/receive loops should treat cancellation-induced `SocketException` as completion, and the shared streams should tolerate restart. Downstream workaround (dashboard): overlap the old and new subscriptions so the count never reaches zero.
5. `SSDP.UPnP.PCL`: `ControlPoint.Start` is neither idempotent nor thread-safe (unsynchronized `IsStarted`, throws on the second call) - the sole reason for `UpnpClient._startLock` downstream (2026-07-25 lock review). Fix flavors: minimal - an `Interlocked` gate making repeat calls no-ops; idiomatic - remove explicit `Start` and let the shared observables start lazily on first subscription (`Defer`), deleting the start-once concept from the stack. Either removes a downstream lock.

---

*Context for the new session: this plan was written from the repository of `SSDP.UPnP.PCL` v7.0.2 immediately after its .NET 10 modernization, functional rewrite, UDA 2.0 compliance review, and release via Trusted Publishing. The conventions above are not aspirations — they are the working practices of that codebase, and the new project should feel like a sibling. Unlike its siblings — hand-built over years — UPnP.Rx is AI-assisted from day one: the plan, CLAUDE.md and CODEMAP.md exist so that every session (human or AI) starts with the same context the author carries in their head for the older libraries.*
