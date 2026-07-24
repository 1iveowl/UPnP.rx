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

## FluentUI Blazor doc-compliance review (2026-07-24, against the official repo README; author decided same day: F1/F4/F5 applied, F2/F3/F6/F7 left as documented positions)

| # | Finding | Doc guidance vs. us | Recommendation |
|---|---|---|---|
| F1 | `AddFluentUIComponents()` registered in the **client only** | Docs register it in the server `Program.cs` (Blazor Server variants also need `AddHttpClient()` first). Nothing breaks today because our server-rendered pages (layout, error) use no Fluent components - but any future server-rendered Fluent usage would fail confusingly. | Add to server too (2 lines, future-proofs) |
| F2 | **Web-components script tag absent** from `App.razor` | README says to add `...lib.module.js` (`type="module" async`) explicitly. It works for us anyway because Blazor Web App JS initializers auto-load it (verified working headlessly). | Leave as-is; revisit only if a component misbehaves - adding it risks double-loading alongside the initializer |
| F3 | **No providers** in the layout | Docs list Toast/Dialog/Tooltip/MessageBar/Menu providers "at the end of MainLayout", with "remove those you are not using". We use none of those features → compliant. | Add `FluentToastProvider` only when the reconnect-toast backlog item lands |
| F4 | **Shared static `Icon` instances** for sun/moon | Doc examples always create icons inline (`new Icons...()` per render); `Icon` instances can carry per-use state (e.g. color), so static sharing is off-pattern. | Switch to inline creation (trivial) |
| F5 | `FluentSearch` uses `Immediate` without **`ImmediateDelay`** | The documented filter pattern debounces (e.g. 200 ms); we re-filter the DynamicData cache every keystroke. Harmless at LAN scale. | Add `ImmediateDelay="200"` (trivial) |
| F6 | Hand-rolled cards/header instead of `FluentCard`/`FluentAccordion`/`FluentDataGrid` | Deliberate divergence, already recorded: custom accordion semantics + token CSS. Docs offer no rule against it. | Keep (conscious choice); `FluentDataGrid` noted as an alternative if the roster ever needs sorting/columns |
| F7 | `reboot.css` linked via `@Assets` fingerprinting | Docs show a plain `/_content/...` link; ours is the `MapStaticAssets`-aware form. | Keep ours (better) |

## Two views: Browser + Port mapper (2026-07-24; author chose ROUTED PAGES over FluentTabs - deep links + room for the 4.0 eventing page; implemented same day)

**Implemented as designed below**, with routed pages (`/` and `/portmapping`), a shared
`DashNav` component (brand, nav links, theme toggle + `FluentDesignTheme`), `GatewayService`
holding auto-renewing leases server-side and broadcasting their `Events` as `LeaseEvent`,
hub RPCs, a `ReactiveCommand`-driven `PortMappingViewModel` (load/add/delete, `WhenAnyValue`
form validation, DynamicData-bound live event feed), and `FluentDataGrid` for the mapping
table (the F6 "when it needs columns" case arrived).

**Scanning empty-state (author spec, same day)**: spinner + "scanning" immediately; after
~5 s with zero devices a hint panel appears (Windows `net stop SSDPSRV` / `net start
SSDPSRV` block, container/VPN/network hints) while stating the server keeps scanning; the
panel disappears the moment a device arrives. Implemented reactively in the view model -
`Timer(5s).CombineLatest(CountChanged, elapsed && count == 0).DistinctUntilChanged()` - no
lifecycle plumbing.

**Ecosystem findings (recorded for posterity):**
- **ReactiveUI 23 + .NET 10 Blazor WASM**: `RxSchedulers.MainThreadScheduler` and
  `ReactiveCommand`'s *default* output scheduler resolve to a WASM scheduler whose type
  initializer throws (`System.Reactive` `WasmRuntime`: "does not support this version of the
  WebAssembly scheduler" - Rx 7 predates the .NET 10 WASM runtime version). Workarounds:
  `DefaultScheduler.Instance` for timers, explicit `outputScheduler:
  CurrentThreadScheduler.Instance` on every ReactiveCommand (WASM is single-threaded).
  Candidate upstream issue against dotnet/reactive / ReactiveUI.
- The on-page error capture added earlier paid for itself twice here - both scheduler
  failures were read straight off the page.

Original proposal follows for reference:

The dashboard currently mirrors Sample.Browser only; the flagship port-mapping story deserves
the same visual treatment. Proposed shape:

- **Navigation**: `FluentTabs` on the one page - tab "Network" (current device browser
  unchanged) and tab "Port mapping".
- **Server**: a `GatewayService` that resolves the gateway with
  `PortMapper.DiscoverGateways(client)` over the *same* `UpnpClient` the discovery service
  owns (showcases the caller-owned-client overload). Hub RPCs:
  `GetGatewayInfo` (friendly name, WAN service, external IP, `ConnectionStatusInfo`),
  `GetPortMappings` (enumeration → array),
  `AddPortMapping(ext, int, protocol, description, leaseSeconds)` - the server creates and
  *holds* the auto-renewing `PortMappingLease`, forwarding its `Events` to all browsers as a
  `LeaseEvent` broadcast (live renewal ticks in the UI - the lease observable made visible),
  `DeletePortMapping` (disposes a held lease, or plain delete for foreign mappings).
- **Client tab**: gateway status card (Connected badge green/red, external IP, uptime);
  mappings table - a natural first use for `FluentDataGrid` (per review F6's note);
  add-mapping form (`FluentTextField`/`FluentNumberField`/`FluentSelect`/`FluentButton`);
  live lease-event feed.
- **Caution to document**: this makes the sample able to *modify the router* from any browser
  that can reach the server - fine for a LAN tool, but the README note should say so
  explicitly (no auth in the sample).
- **Verification limits**: headless can only prove the tab renders and the no-gateway empty
  state behaves; add/delete needs the author's real router.

1. **Per-service action invocation** in the expanded view - SCPD-driven form via
   `ValidateAndOrderArguments`.
2. **Per-device SSDP message log** - pending the open question above.
3. **4.0 eventing hook**: when GENA lands, subscribe evented state variables and update the
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
