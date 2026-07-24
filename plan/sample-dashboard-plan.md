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

## Iteration backlog (for author review)

1. **Device detail flyout** - click a card → `FluentDialog` with the full device tree and
   per-service action invocation (SCPD-driven form via `ValidateAndOrderArguments`).
2. **v2 eventing hook**: when GENA lands, subscribe evented state variables and update cards
   live - the dashboard becomes the eventing demo.
3. **Roster v1.1**: replace the sample's ConcurrentDictionary with the library's live roster
   observable once it exists; delete the byebye/alive trade-off note above.
4. Reconnect UX polish (toast on reconnect, stale-badge while disconnected).
5. Screenshot/GIF in the README once the author has run it against a real network.
