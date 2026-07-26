# UPnP.Rx 4.1.0 plan - roster, typed AV events, device interaction (drafted 2026-07-25)

**Design-for-review: nothing below is implemented until the author signs off on the scope
and the open questions in §5.** Consolidates the deferred 3.1/4.1 backlog
(`upnp-rx-v3.1-plan.md` remains the historical record; items that shipped meanwhile are
marked), plus the new headliner the author requested: engaging with devices - volume,
transport - from the dashboard without an explosion of new code.

## 1. What 4.1 is

A minor release with two library headliners and one sample headliner:

1. **Device roster** (library) - the long-promised presence structure with expiry.
2. **Typed `LastChange` helper** (library) - AV eventing payloads become usable.
3. **Device interaction** (dashboard) - invoke any action generically; curated quick
   controls (volume, transport) for the two AV services everyone actually touches.

Everything else is ergonomics, hygiene, or recorded reflection.

## 2. Library work

### 2.1 The roster (headliner; solves four recorded gaps at once)

`UpnpClient.Roster()` - a shared, replaying stream of device presence:

- **Shape (Q1):** a closed union + last-known replay, exactly the pattern the 4.0 eventing
  engine proved: `RosterChange` = `DeviceAppeared(DiscoveredDevice)` |
  `DeviceUpdated(DiscoveredDevice)` | `DeviceExpired(Usn)` | `DeviceLeft(Usn)`; late
  subscribers first receive the current roster flagged as replay, then live changes -
  the `GenaSubscriptionSource` gate/replay machinery generalizes almost verbatim
  (per-key state, one gate, hot-shared with ref-count lifecycle). No new dependency:
  DynamicData stays a *consumer-side* choice (the dashboard may keep using it), not a
  library contract (§5 dependency lock).
- **Expiry on the one clock:** per-device deadline from `CACHE-CONTROL: max-age`
  (fallback default when absent), driven by `TimeProvider` - a silently vanished device
  finally becomes `DeviceExpired`. Alive announcements refresh the deadline.
- **Solves the recorded gaps:** unbounded `Distinct` state on long-lived subscriptions
  (the roster replaces them); byebye→alive-with-same-BOOTID re-emission (roster keys on
  USN, not USN#BOOTID); silent vanish (expiry); and the dashboard's server-side
  `ConcurrentDictionary` roster becomes a thin library consumer.
- **Self-healing folds in here (Q2), the cheap way:** on an alive announcement whose
  device's description-cache TTL has lapsed, re-describe lazily; emit `DeviceUpdated`
  only when the parsed records differ (value-equal records make the comparison free).
  No proactive timers, no extra LAN chatter - it piggybacks on traffic that already
  happens. The dashboard's Rescan button stays as the manual big hammer.

### 2.2 Typed `LastChange` (headliner enabler; 4.0 decision Q4 comes due)

AVTransport and RenderingControl event through a single `LastChange` variable carrying
escaped XML-in-XML. 4.0 passes it through verbatim; 4.1 adds the decoder:

- `UPnP.Rx.Eventing.Av.LastChangeParser.Parse(string) →
  ParseResult<IReadOnlyList<AvPropertyChange>>` with
  `AvPropertyChange(InstanceId, Name, Value, Channel?)` - pure, total, lenient per house
  policy (namespace/case-tolerant, values stay strings, `channel="Master"` surfaced
  because volume needs it). Plus a discoverability sugar:
  `service.Events().SelectAvChanges()` extension flattening `PropertyChange` events named
  `LastChange` into `AvPropertyChange` streams.
- Fixtures: real captured Sonos AVTransport/RenderingControl NOTIFYs (the author's
  network; `Sample.Eventing` prints raw values today - capture from there), plus
  malformed variants.

### 2.3 Ergonomics and hygiene (small, batched)

- `TryService(...)` + service enumeration by type on `DescribedDevice`.
- Typed SOAP argument conversions (`int`/`bool`/`TimeSpan` per SCPD dataType) - **only
  if** the quick-controls work (§3) proves the need; otherwise stays deferred.
- Trimming annotations audit (publish is warning-clean today; make it deliberate).
- RX-6 leftover: surface a dropped-events counter on the dashboard hub channel (or
  record "sample-grade cap, accepted" and close it).

## 3. Device interaction (the author's new ask, sized deliberately)

**How many controls are there?** As many as the SCPDs declare - and the dashboard already
lists every one. Concretely on a Sonos renderer: `AVTransport:1` ≈ 35 actions (Play,
Pause, Stop, Next, Previous, Seek, SetAVTransportURI, GetPositionInfo, …),
`RenderingControl:1` ≈ 30 (SetVolume, GetVolume, SetMute, SetRelativeVolume, bass,
treble, loudness, …), plus ConnectionManager and Sonos extras (Queue,
GroupRenderingControl with *group* volume). Across a whole network: hundreds. The answer
to "how do we not go overboard" is therefore: **one generic mechanism for all of them,
plus a curated thin veneer for exactly two services.**

- **3a - Generic action invocation (small; the 90% case).** The SCPD action rows the
  cards already render become invocable: clicking an action unfolds a form generated
  from its in-arguments (the parser already knows each argument's related state
  variable, dataType, allowedValueList and range - `ServiceDetailDto` grows those
  fields; enum → dropdown, boolean → switch, range → number input). Submit → one new
  hub RPC `InvokeAction(deviceKey, udn, serviceType, action, args)` →
  `scpd.ValidateAndOrderArguments` + `service.InvokeAsync` (both exist since 3.0; this
  finally exercises them end to end) → out-arguments rendered as a result table,
  `UpnpError` rendered as the device's own words. New code: one hub method, one form
  component, DTO fields. **Safety (Q3):** actions mutate real devices - a confirm-step
  for actions with in-arguments, and the existing "your network, your devices" framing
  in the README.
- **3b - Curated quick controls (thin; the daily-driver case).** For *recognized*
  service types only, a small control strip on the service row: RenderingControl →
  volume slider + mute toggle (`SetVolume`/`SetMute`, InstanceID 0, channel Master);
  AVTransport → play/pause/next/previous + transport-state chip. Live state rides the
  4.0 eventing engine through the new `LastChange` parser (§2.2) - the slider follows
  volume changes made on the device itself, which is the demo that sells the whole
  library. Hardcoded to those two service types by design; everything else goes through
  3a. New code: one component + the §2.2 parser it consumes.

## 4. Carried reflections and upstream items

- **Shipped meanwhile (closed):** reconnect toast (4.0 E6); `ReactiveUI.SourceGenerators`
  for settable VM properties (Rx/ReactiveUI review RUI-7); rescan-as-manual-heal;
  upstream candidates #3/#4/#5 (SHL 7.2.0/7.3.0, SSDP 8.0.0).
- **Open upstream filings (Q7):** dotnet/reactive - Rx 7 WASM scheduler rejects .NET 10;
  ReactiveUI - `WithBlazorWasm()` registers that broken default; SSDP.UPnP.PCL - verify
  whether 8.0.0 already addressed `ConfigureAwait(false)` (candidate #1) and the
  `Device` `IAsyncDisposable` story (candidate #2), file whatever remains; SSDP README
  SSDPSRV hint (docs).
- **CommunityToolkit.Mvvm / R3 reflections stand** (v3.1 doc §6). The recorded decision
  point - "re-decide at the roster work, which reshapes the view models" - is now (Q6).
  Recommendation: stay on ReactiveUI; the 4.0 review verified the setup current and
  correct, and the source-generator adoption removed most boilerplate arguments.
- **SSDP message log** (Q5) - resolved 2026-07-25 by splitting the intent: the **device
  activity timeline** shipped (library `Announcements()` - parsed envelopes,
  undeduplicated, kind-tagged, clock-stamped, passive and live-only; dashboard shows a
  capped per-card feed). The **raw wire log** stays deferred and, per the author, belongs
  upstream anyway (raw headers live in SimpleHttpListener.Rx's layer) - an upstream
  feature candidate for whenever it is wanted.
- README screenshot/GIF from a real network (author's step, any time).

## 5. Open questions for the author - ANSWERED (2026-07-25)

**Q1 union+replay · Q2 fold into roster · Q3 confirm-step · Q4 Av namespace + sugar ·
Q5 message log dropped (later release) · Q6 ReactiveUI stays · Q7 upstream filings later.**
Original questions preserved below.


- **Q1 - roster shape:** closed union + replay (recommended, no new dependency, reuses
  the proven engine pattern) - or a DynamicData-style keyed cache as the public contract?
- **Q2 - self-healing:** fold into the roster as re-describe-on-alive-after-TTL with
  value-change detection (recommended) - or keep manual-only for 4.1?
- **Q3 - action invocation safety:** confirm-before-invoke for actions with in-arguments
  (recommended), or invoke-immediately with an undo-is-your-problem stance?
- **Q4 - LastChange surface:** separate `UPnP.Rx.Eventing.Av` namespace with parser +
  `SelectAvChanges()` sugar (recommended) - or parser only, no sugar?
- **Q5 - SSDP message log:** still wanted? If yes it needs a library-level raw tap -
  scope it here or drop it.
- **Q6 - ReactiveUI:** stay (recommended) or take the CTM migration alongside the
  roster-driven view-model reshape?
- **Q7 - upstream filings:** file the two Rx-WASM issues now or after 4.1?

## 6. Phases (one commit each, on dev/4.1.0; version bump in R1)

**Status 2026-07-25: R1-R6 implemented (149 tests green; roster loopback against fakes,
LastChange fixtures synthetic-but-faithful pending author captures). §2.3 outcomes: typed
SOAP conversions stay deferred (the quick controls were comfortable with strings), RX-6's
64-event hub cap recorded as accepted sample-grade behavior, trim/AOT declared and
analyzer-clean. "Service enumeration by type" from §2.3 is DROPPED, recorded: `Services`
is already public and the filter is one consumer-side LINQ line - `TryService` was the
ergonomic that mattered. R7 awaits the author's hardware validation (quick controls +
action forms against real Sonos; capture LastChange fixtures while at it) and the
release go.**

| Phase | Deliverable |
|---|---|
| R1 | Version 4.1.0; roster core: `RosterChange` union, expiry on TimeProvider, replay - FakeControlPoint + FakeTimeProvider tests |
| R2 | Self-heal fold-in (per Q2); dashboard adopts the library roster (server dictionary retires; rescan stays) |
| R3 | `LastChange` parser + `SelectAvChanges()` + Sonos fixtures (author captures) |
| R4 | Dashboard: generic action invocation (DTO fields, hub RPC, SCPD-driven form, result/fault rendering) |
| R5 | Dashboard: quick controls for RenderingControl/AVTransport, live via R3 |
| R6 | Ergonomics batch (§2.3) + README (controls sections, screenshot slot) |
| R7 | 4.1.0 release: releases/4.1.0 branch, tag, Trusted Publishing + GitHub release |

## 7. Post-implementation review (2026-07-25, same day; all findings fixed)

Self-review pass over the new 4.1 code, per the author's instruction. Findings, all
implemented in the review commit:

- **RV-1 (roster, concurrency)** - concurrent announcements for one key could run two
  self-heals and double-emit `DeviceUpdated` (the same don't-trust-device-politeness
  reasoning as the eventing RX-1 fix). One heal per key at a time now, guarded by the gate.
- **RV-2 (dashboard, robustness)** - a sloppy SCPD repeating an argument name crashed the
  action form's dictionary build; `TryAdd` first-wins per leniency. Enum inputs also
  prefill their first allowed value, so dropdowns always hold a valid choice.
- **RV-3 (dashboard, UX/network)** - the volume slider fired one `SetVolume` per step;
  now coalesced latest-wins (a drag sends a handful of calls, not one per pixel), and
  live volume sync no longer fights the user's drag.
- **RV-4 (hub)** - the `LastChange` expansion matched the name case-sensitively; devices
  vary - now `OrdinalIgnoreCase`, matching the library extension.
- **RV-5 (test coverage)** - the heal-stays-quiet test accidentally exercised expiry
  rather than the heal path (its clock advance also lapsed the roster deadline); retimed
  to genuinely cover "cache lapsed, content identical → silence".
- **RV-6 (docs)** - stale hub comments from the pre-roster architecture updated; the
  guarded `.Result` in the cache-state read annotated as known-completed (rule 3).

Verified clean on re-read: the roster engine's gate discipline (mirrors the reviewed
eventing engine), `Roster()`'s lock-free create-once, the DTO/JSON surface, the confirm
flow, `SelectAvChanges` purity.

## 7b. Memory and performance audit (2026-07-25, author-requested)

Principle verified: every stateful structure is sized by what is PRESENT (devices,
variables, subscribers), never by what PASSED THROUGH (announcements, notifies,
subscribe cycles). Bounded by design and now pinned by soak tests (SoakTests.cs):
roster entries (expiry removes), eventing last-known (per variable), observer lists,
activity ring (20/device), live-event ring, held leases, hub channels (64, DropOldest).

Three real accumulations found and fixed: (1) the description cache kept one described
tree per boot generation forever - fresh generations now evict superseded ones for the
same location; (2) the client's activity rings for departed devices lingered - dropped
on DeviceGone, plus an age bound (1 h relative to the newest row - stale rhythm misleads
more than it informs); (3) the expanded-row set accumulated click residue - crudely
capped. Accepted as bounded-and-fine: EventingContext's one source per ever-watched
eventSubUrl (network-sized); activity rings persisting across rescans (log continuity is
the point). Retention config = the two documented constants in DeviceStreamClient -
sample-grade by intent; long retention belongs in a file log, deliberately deferred.

## 7c. Dedup review (2026-07-26, on dev/4.1.1; author-initiated, then extended)

The author's review commit removed duplication piecewise (shared ampersand-recovery
helpers, IsEnabled guards, observer-snapshot helper); a follow-up carried each pattern to
every remaining instance, then a general dedup pass consolidated the structural twins:

- **`EngineSource<TEvent>`** - the eventing and roster engines shared a ~100-line
  lifecycle skeleton (first-subscriber start, last-disposal cancel, replay-under-gate,
  emit/error/shutdown, the reentrant-Lock dependency note). One audited base class now
  owns it; the engines keep only their actual engines and replay/clear hooks. A third
  engine becomes cheap.
- **`TimedExchange`** - the timeout-CTS + exception-translation triple (lifetime →
  ObjectDisposedException, timeout → UpnpException, HttpRequestException → wrapped) lived
  four times (InvokeAsync, SCPD fetch, description fetch, GENA transport); now once, with
  per-site messages preserved byte-identically.
- **`TestKit`** - WaitForAsync existed five times, Fixture four times, SettleAsync twice;
  now once in TestHelpers (the loopback tests keep their real-time variant by design).
- Listener responses go through one local AnswerAsync.
- **Noted, not done:** the local-IPv4-interface enumeration exists in PortMapper (private)
  and the dashboard's NetworkClientProvider - unifying needs a public helper, i.e. new
  API, which does not belong in a patch. 4.2 candidate.

## 8. Risks

- **Roster correctness is where subtle bugs live** (the reason it was deferred twice):
  expiry vs. renewal races, replay-vs-live atomicity, USN identity across boots. The
  eventing engine's tested gate pattern and one FakeTimeProvider per test are the
  mitigation - and the reason the roster should reuse that machinery rather than invent.
- **LastChange dialects:** Sonos is the reference but other renderers differ (InstanceID
  nesting, val attributes, channel handling). Leniency policy + fixtures-from-the-wild.
- **Action invocation is a loaded gun by design** - the confirm step (Q3) and framing
  keep the sample honest.
- **Scope creep in quick controls:** two services, hardcoded, no plugin system - anything
  more is 4.2 territory.
