# Sample.Dashboard plan (recorded 2026-07-24; initial implementation landed the same day)

The working document for the Blazor dashboard sample. The first cut was built in the same
session that designed it (author's call: continue, record the plan after the fact); this doc
is the reference for iterating on it.

## Goal

A visual showcase of what makes UPnP.Rx different: **device presence as observable streams**.
Devices appear and disappear live as they join and leave the network - the thing a console
sample cannot show. Also serves as a reference for the realistic production topology: a
backend on the LAN doing the listening, a UI anywhere.

## Architecture (decided)

- **Server does the network, WASM does the pixels.** The browser sandbox has no UDP/multicast,
  so discovery cannot run client-side. `Sample.Dashboard` (Blazor Web App host, WebAssembly
  interactivity) runs the one `UpnpClient`; `Sample.Dashboard.Client` (WASM) renders.
- **SignalR is the seam**: hub at `/devicehub`, messages `DeviceUp(DeviceDto)` /
  `DeviceGone(key)`. The hub replays the current roster to each newly connected client
  (`OnConnectedAsync`), so late joiners see the full picture; the client clears + replays on
  reconnect.
- **Rx end to end**: UPnP.Rx observables (server) → SignalR → DynamicData
  `SourceCache<DeviceDto, string>` keyed by normalized UDN (client) → `SortAndBind` →
  ReactiveUI `ReactiveObject` view model (`Count`/`Status` as OAPHs) →
  `ReactiveInjectableComponentBase` page → FluentUI cards.
- **House rules apply in samples**: no async in `Subscribe` - the server pipeline uses
  `SelectMany(Observable.FromAsync(...))` for describe + broadcast; per-candidate failures
  are `Catch`→`Empty` with a log note.

## Design notes and known trade-offs

- The server uses `DiscoverDevices()` (dedup by USN+BOOTID), *not*
  `DiscoverDescribedDevices()` (dedup by UDN for the stream's lifetime): periodic re-announcements
  must be able to re-add a device that a byebye removed. Residual gap: byebye followed by
  alive within the same boot is suppressed by the per-subscription dedup - the structural fix
  is the v1.1 roster (plan §8 decision 4), which this sample is the first consumer for.
- `DeviceDto` is flat (root identity + distinct service types across the tree + tree size),
  shared by both projects from the Client assembly. Keys are normalized UDNs (lowercase,
  `uuid:` stripped) so byebye USNs match description UDNs.
- Multicast constraints apply unchanged: run the server on a real host, not a container.
  Windows needs SSDPSRV paused (see README Troubleshooting).

## Packages (sample-only; library dependency policy untouched)

`Microsoft.FluentUI.AspNetCore.Components` 4.14.3, `DynamicData` 9.4.33,
`ReactiveUI.Blazor` 23.2.28, `Microsoft.AspNetCore.SignalR.Client` 10.0.10.

## UI specification (author, 2026-07-24; implemented same day)

- **Collapsed by default**: the list shows one heading row per device - name, maker/model,
  service + embedded-device count badges, chevron. Nothing else.
- **Unfold on click**: expanding a device shows its content "neatly presented" - an identity
  block (type, UDN, location link) followed by the device's tree.
- **Drill-down via sub-headers**: devices with large trees (e.g. a Vera Z-Wave gateway with
  dozens of embedded devices) expand level by level - each embedded device is itself a
  collapsed sub-header (name, service/device counts) that unfolds on click. Services render
  as monospace rows.
- **Filter box**: live text filter over name/manufacturer/device type, implemented as a
  DynamicData `Filter` fed by `WhenAnyValue(Filter)` - the search showcases Rx too.
- **Render pump**: `Revision` OAPH ticks on every cache changeset so the page re-renders on
  adds, removals *and in-place updates* (fixes a gap where an updated device with an
  unchanged count would not re-render).
- **ErrorBoundary** wraps the page: a client-side exception now renders its type + message
  in an inline panel instead of only the generic "unhandled error" banner - both a UX and a
  diagnosability fix.
- Wire format: `DeviceDto` carries the **full tree** (`DeviceNodeDto` recursion) so
  drill-down needs no extra round trips.

**Open question for the author**: "all messages for that device" is currently interpreted as
the device's *description content* (the tree above). If it should mean the raw **SSDP
messages** observed per device (alive/byebye log with timestamps), the library needs a small
addition first - `UpnpClient` does not expose the raw announcement stream per device; that
would be a tap on `IControlPoint`'s observables surfaced per-UDN. Flag which reading is
intended.

## Code review: ReactiveUI + FluentUI usage (2026-07-24; fixes applied same day)

**ReactiveUI**
1. **View-model leak (fixed)**: `ReactiveComponentBase` could not be shown to dispose the
   injected view model, so each mount of the transient VM would leave its DynamicData
   subscriptions attached to the singleton cache forever. The page now implements
   `IDisposable` and disposes its VM explicitly (safe even if the base also does - 
   `CompositeDisposable` tolerates double-dispose). Transient remains the right lifetime:
   durable state (hub connection, roster cache) lives in the singleton service; the VM is a
   cheap per-view projection.
2. **Double `Connect()` (fixed)**: the `Revision` render pump ran its own second changeset
   subscription. Now one pipeline: `Connect → Filter → SortAndBind → Select(index) →
   ToProperty` - single subscription, and the pump also ticks when the filter changes the
   visible list.
3. **Mutable internals exposed (fixed)**: `DeviceStreamClient` returned its `SourceCache` and
   `BehaviorSubject` directly - callers could mutate the roster or complete the state stream.
   Now `IObservableCache` (`AsObservableCache`) and `AsObservable()`.
4. Hub event names were duplicated string literals on both sides (fixed: `HubEvents` consts
   in the shared client assembly).

**FluentUI**
5. **The visual model (adopted): FluentUI design tokens.** Custom CSS hardcoded hex colors
   next to Fluent components. All custom styling now uses Fluent's CSS custom properties
   (`--neutral-layer-*`, `--neutral-foreground-*`, `--accent-fill-rest`,
   `--neutral-stroke-*`), with `FluentDesignTheme` as the token source. This is what makes
   the UI cohere with the Fluent components - and makes dark mode nearly free, since tokens
   flip with the theme.
6. Raw `<input>` for the filter replaced with `FluentSearch` (Immediate) - correct Fluent
   input semantics, styles itself from tokens.
7. Layout polish: consistent spacing, token-driven card elevation/borders, accent stripe from
   `--accent-fill-rest`.

**Feature: dark/light mode switch** (revised after author feedback)
- **Follows the OS/browser preference on launch**: mode starts as System;
  `OnLoaded`/`OnLuminanceChanged` report the *effective* luminance (they carry `IsDark`), so
  the toggle always reflects what is actually on screen - including what System resolved to.
- **Icon button, not a labeled switch**: sun/moon `FluentIcon` in a stealth `FluentButton`
  (icons from the `...Components.Icons` package; note the icon classes live in
  *sub-namespaces*, so fully qualify - a plain `using` of the parent does not import them).
- An explicit choice persists via `StorageName`; System applies again only until first use.
- **Accent identity**: `CustomColor="#0891b2"` (cyan-teal) on `FluentDesignTheme` - every
  accent token (card stripes, chevrons, badges, icons) follows it in both modes; swappable
  for an `OfficeColor` palette entry.
- Verified with headless Chromium using `colorScheme` emulation: fresh profile with OS-dark
  starts dark (icon offers light), OS-light starts light, toggle flips, accent token
  propagates, zero console errors; screenshots reviewed.

**Feature: service drill-down** (author: "is a name all there is?") - it is not: services
unfold. Expanding a service row fetches its SCPD on demand over a hub RPC
(`GetServiceDetail(deviceKey, owningUdn, serviceType)` - owning UDN disambiguates repeated
service types across embedded devices, e.g. `SwitchPower:1` on every light). The server keeps
the live `DescribedDevice` per roster key and uses the library's cached `GetScpdAsync`; the
client renders Actions (name + in/out argument names) and State variables (name, data type,
allowed values), with a spinner while loading and inline errors. Action *invocation* from the
UI stays on the backlog.

**Presentation fix** (author: "Sonos Five has three devices but two sub-headers") - nothing
was missing: `DeviceCount` includes the root device, which the card itself represents; only
embedded devices appear as sub-headers. The badge now says "N embedded" (count minus root)
and node summaries say "embedded" too.

## Iteration backlog (for author review)

1. **Per-service action invocation** in the expanded view - SCPD-driven form via
   `ValidateAndOrderArguments`.
2. **Per-device SSDP message log** - pending the open question above.
3. **v2 eventing hook**: when GENA lands, subscribe evented state variables and update the
   expanded view live - the dashboard becomes the eventing demo.
4. **Roster v1.1**: replace the sample's ConcurrentDictionary with the library's live roster
   observable once it exists; delete the byebye/alive trade-off note above.
5. Reconnect UX polish (toast on reconnect, stale-badge while disconnected).
6. Screenshot/GIF in the README once the author has run it against a real network.

## Debugging note (RESOLVED)

The blank page + generic error banner had a deterministic root cause, found by driving the
served app with headless Chromium and capturing the console: **ReactiveUI 23 requires
explicit builder-pattern initialization** (`RxAppBuilder.CreateReactiveUIBuilder()
.WithBlazorWasm().BuildApp()` before anything calls `WhenAnyValue`). Without it, the
property-observation mixin throws a `TypeInitializationException` during page construction -
which an ErrorBoundary inside the page can never catch, hence the blank page. The old view
model had escaped this by accident (a `ToProperty`-first code path); adding the filter's
`WhenAnyValue` surfaced it. Fixes in place:

- Explicit ReactiveUI builder init at WASM startup (the actual fix).
- `TrimmerRootAssembly` roots for ReactiveUI/Splat (Release-publish safety).
- On-page error surfacing: a console.error/window-error hook writes the real exception text
  into the Blazor error bar - construction-phase failures are readable without F12.
- Verified with the headless probe: page renders, hub connects ("live"), no console errors.

Debugging technique worth keeping: `scratchpad` Playwright probe (launch server, capture
console/pageerror/failed requests, dump body + error bar). Candidate: check a variant into
`tools/` as a smoke test for the dashboard.
