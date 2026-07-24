# UPnP.Rx v3.1 plan (drafted 2026-07-24; 3.0.0 released to NuGet the same day)

> **Superseded in sequencing (2026-07-24):** the author pulled GENA eventing forward as the
> next release (4.0.0, see plan/upnp-rx-v4.0-eventing-plan.md); the candidates below defer
> and will most likely ship as 4.1.0. Content preserved unchanged as the record.

The working document for the first post-3.0 iteration. Candidates are collected here as they
surface; the author decides scope before implementation, per house workflow. Items called
"v1.1" in older documents map here (the version scheme changed to 3.0 - see plan §8
decision 2); GENA eventing remains the next-major headliner (4.0).

## 1. Device roster (the headliner; formerly "v1.1 roster")

`IObservable<IReadOnlyList<DiscoveredDevice>>` (or a DynamicData-style keyed cache) on
`UpnpClient`, tracking alive/byebye/CACHE-CONTROL max-age expiry per device. Solves, in one
structure, the recorded gaps that keep resurfacing:

- unbounded `Distinct` state on long-lived `DiscoverDevices`/`DiscoverGateways`
  subscriptions (pre-publish review finding 11);
- re-emission after byebye→alive within one boot (dashboard plan trade-off note);
- advertisement expiry (a device that silently vanishes is currently never reported lost);
- the dashboard's server-side ConcurrentDictionary roster becomes a library consumer.

## 2. Self-healing descriptions - INVESTIGATION, not yet a commitment

Shipped in 3.0 already: BOOTID in the description cache key (reboot re-reads), CACHE-CONTROL
max-age TTL on cache entries (a sparse read heals within one advertisement cycle),
`InvalidateDescriptions(location)` escape hatch, and a manual per-card refresh in the
dashboard.

The open question is *automatic* re-description and roster updates without user action.
The author's caution (2026-07-24): **this may be overkill for the sample** - a dashboard that
re-fetches every device every advertisement cycle multiplies LAN chatter for marginal UX
gain. Investigate before building:

- Where does self-healing belong - library (roster emits updated devices when a re-read
  changes content) or consumer? Leaning library-roster, since every consumer would otherwise
  reinvent it.
- Trigger options: re-describe on alive after the TTL lapsed (cheap, piggybacks on traffic
  that already happens) vs. proactive timers per device (chatty; probably reject).
- Change detection: avoid roster churn when a re-read is byte-identical; compare parsed
  records (they are value-equal records - cheap).
- Measure: how often do real devices serve materially different descriptions within a boot?
  (The Sonos incident suggests rarely - once, at boot.)

## 3. Library candidates

- **Typed `LastChange` helper** (from 4.0 eventing decision Q4): parse AVTransport/
  RenderingControl's escaped XML-in-XML event payloads into typed property changes - core
  eventing stays generic in 4.0.

- **Typed SOAP argument helpers**: `ValidateAndOrderArguments` shipped in 3.0; consider a
  typed layer (`int`/`bool`/`TimeSpan` conversions per SCPD dataType) if consumer demand
  appears.
- **`DescribedDevice`/service ergonomics**: `TryService(...)`, service enumeration by type.
- **Trimming annotations audit** for the library itself (publish is warning-clean today).

## 4. Dashboard backlog (carried from the sample plan)

- Per-service **action invocation UI** (SCPD-driven form via `ValidateAndOrderArguments`).
- Per-device **SSDP message log** - blocked on the open question in the sample plan
  (needs a raw announcement tap in the library; author to confirm the intent).
- Reconnect toast (`FluentToastProvider` arrives with it - FluentUI review F3).
- Screenshot/GIF for the README from a real network.

## 5. Upstream issue candidates (file before or alongside 3.1)

1. **SSDP.UPnP.PCL**: no `ConfigureAwait(false)` (0 of 26 awaits) - plan §9.
2. **SSDP.UPnP.PCL**: `Device` sync-`Dispose`/`ByeByeAsync` footgun - `IAsyncDisposable`
   candidate (disposal model note).
3. **dotnet/reactive**: Rx 7's WASM scheduler enlightenment rejects the .NET 10 runtime
   ("does not support this version of the WebAssembly scheduler") - breaks ReactiveCommand
   defaults on Blazor WASM (dashboard plan, ecosystem findings).
4. **SSDP.UPnP.PCL README**: the Windows SSDPSRV pause hint (docs-only).

## 6. Technology landscape reflections (recorded 2026-07-24, post-3.0.0 release; download counts as of that date)

**Decision: the dashboard stays on ReactiveUI for now.** The author weighed a switch and
chose continuity - the current setup works, its quirks are documented, and the alternatives
have real losses. Recorded so the reasoning survives:

**CommunityToolkit.Mvvm (24.9M downloads vs ReactiveUI's 18.9M; CTM's velocity is far
higher - four years vs fourteen to those totals; Microsoft-maintained, source-generated,
inherently trim/AOT-safe - the WasmRuntime/builder-init failure class cannot occur in it).**
- What a switch would buy: deletion of every ReactiveUI workaround we wrote (explicit
  command schedulers, RxAppBuilder init, trimmer roots), mainstream docs/support, simpler
  commands (`[RelayCommand]`, `IsRunning`).
- **What would be lost (why we stayed)**: `WhenAnyValue`/`ToProperty` declarativeness - CTM
  has no stream algebra, so stream→property plumbing becomes explicit subscriptions; and the
  "Rx end to end" narrative of the sample. DynamicData and raw Rx are unaffected either way.
- Migration inventory if ever done (half a day, client only): swap package + delete init and
  trimmer roots; `ReactiveObject`→`ObservableObject`, OAPHs→`[ObservableProperty]` set from
  subscriptions, `WhenAnyValue(Filter)`→`OnFilterChanged` partial pushing a subject;
  ReactiveCommands→`[RelayCommand]` async methods with explicit `await LoadAsync()`
  chaining; pages get a ~20-line INPC→StateHasChanged base component. Server/library
  untouched.
- **Natural decision point: the 3.1 roster work**, which reshapes the view models anyway.

**R3 (Cysharp, 3.7M)** - Rx redesigned on **TimeProvider**, i.e. the same clock model as our
house time model (Rx 7 lacks it; we built policy around the gap and hit its WASM scheduler
rejecting .NET 10). Costs: its own `Observable<T>` (not `System.IObservable<T>`) - a public-
contract change for the library, so only ever a candidate as *internal engine or adapter
package* (`UPnP.Rx.R3`), never as the API. Young; adoption partly Unity-driven. Worth a real
evaluation in 3.1 or later.

**Kept/complementary**: `System.IObservable` stays the library's public contract
(framework-neutral, BCL); DynamicData (21.8M) stays - no equal for collections-over-time;
`IAsyncEnumerable`/Channels complement pull-shaped pipelines but cannot replace hot
multicast streams.

**Functional vocabulary**: CSharpFunctionalExtensions (35.4M) is `ParseResult<T>` grown up
(`Map`/`Bind`/railway) if composition demand appears; LanguageExt (46.9M) is powerful but a
paradigm commitment; OneOf (66.7M) light unions. C# discriminated unions are progressing in
the language - which is why the copied 40-line `ParseResult` (decision 5, zero coupling)
ages well: when the language lands unions, we migrate a record, not a dependency.

**For joy, not roadmap**: Bolero (F# Elmish Blazor, 115K) as the intellectually honest MVU
counterpart; F# consumers of the library already get a good experience from the current API.

## 7. Release mechanics

3.1 follows the house discipline: phase commits on `main`, `releases/3.1.0` branch frozen at
tag, Trusted Publishing on the tag push. The 3.0.0 release must ship first - it is currently
held for the author's own review pass (no tag, no push).
