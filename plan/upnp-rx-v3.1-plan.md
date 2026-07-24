# UPnP.Rx v3.1 plan (drafted 2026-07-24; current version is 3.0.0, unreleased)

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

## 6. Release mechanics

3.1 follows the house discipline: phase commits on `main`, `releases/3.1.0` branch frozen at
tag, Trusted Publishing on the tag push. The 3.0.0 release must ship first - it is currently
held for the author's own review pass (no tag, no push).
