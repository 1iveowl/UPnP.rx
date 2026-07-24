# UPnP.Rx 4.0.0 plan - GENA eventing (drafted 2026-07-24, on branch dev/4.0.0)

**Author decision (2026-07-24):** eventing jumps the queue - 4.0.0 is the next release and
its scope is UPnP eventing only. The former 3.1 candidates (roster, R3/CTM evaluations,
dashboard backlog) defer to 4.1.0; `plan/upnp-rx-v3.1-plan.md` remains their record.
`releases/3.0.0` is frozen. **This document is design-for-review: nothing below is
implemented until the author signs off on the design and the open questions.**

## 1. What we're building (UDA 2.0 clause 4, control-point side only)

A control point SUBSCRIBEs to a service's `eventSubURL`; the device replies with a `SID`
and a `TIMEOUT`, then immediately NOTIFYs the full evented state (SEQ 0) to an HTTP
callback the control point hosts, followed by a NOTIFY per state change (SEQ 1, 2, …).
Subscriptions expire unless renewed; UNSUBSCRIBE is the goodbye. Completing this clause
completes the pitch: *"…watch its state."* Device-side eventing stays out of scope.

## 2. The design

### 2.1 Wire layer - pure, total, fixture-tested (mirrors Phase 1/2 discipline)

- `Eventing/GenaComposer` (pure): SUBSCRIBE request (`NT: upnp:event`, `CALLBACK:
  <http://ip:port/path>`, `TIMEOUT: Second-n`), renewal SUBSCRIBE (`SID` + `TIMEOUT`),
  UNSUBSCRIBE (`SID`). Outbound uses `HttpClient` with custom verbs
  (`new HttpMethod("SUBSCRIBE")`) - no raw sockets needed for the outbound half.
- `Eventing/GenaParser` (pure, `ParseResult<T>`):
  - Subscription responses: `SID`, `TIMEOUT: Second-n` - lenient (`second-1800`, `Second-infinite`,
    garbage → unset; devices are sloppy here).
  - NOTIFY requests: `NT`/`NTS`/`SID`/`SEQ` headers + `e:propertyset/e:property/<Name>value`
    body - namespace/case-tolerant per `XmlLeniency`, values are strings (leniency: nested
    escaped XML like AVTransport `LastChange` passes through verbatim; a typed LastChange
    helper is a 4.1 candidate, not core).
- Fixtures: real captured NOTIFY bodies (Sonos AVTransport/RenderingControl are ideal - the
  author's network can capture them), plus malformed variants per house policy.

### 2.2 Callback listener - the seam (verified feasible 2026-07-24)

- **Confirmed against the upstream source**: `SimpleHttpListener.Rx` emits
  `HttpRequestResponse` carrying a live `IHttpConnection`, and `HttpSender.SendResponseAsync`
  writes the required `200 OK` back. The stack was built for this; no new dependency.
- One shared `TcpListener → ToHttpListenerObservable` per `UpnpClient`, started lazily on the
  first event subscription (same lazy-start discipline as discovery). NOTIFYs route to
  subscriptions by callback path token + `SID`.
- **Testing seam, mirroring `IControlPoint.HotStart`**: the eventing engine consumes
  `IObservable<HttpRequestResponse>` + a response-send delegate - tests drive NOTIFYs from a
  `Subject` and assert responses without any socket. Loopback TCP integration tests are
  allowed (no multicast involved); real-device smoke is the author's step.
- Multihoming: the callback URL's host comes from the device's discovery envelope
  (`DiscoveredDevice.LocalEndPoint` - added in 3.0 for port mapping, reused here). Port via
  `UpnpClientOptions.EventCallbackPort` (0 = ephemeral).

### 2.3 Subscription lifecycle - Rx-first (the design's center)

**The GENA subscription IS the Rx subscription.** Public shape:

```csharp
// On UpnpService:
IObservable<UpnpEvent> Events();
```

- Built on `Observable.Create`'s async-subscribe overload (disposal-model rule 3): subscribe →
  SUBSCRIBE is sent; dispose → UNSUBSCRIBE is sent (the token-fires-on-unsubscribe pattern,
  reference: the SHL UDP listener).
- Shared per service via `Publish().RefCount()`: N Rx subscribers = one GENA subscription;
  the last dispose sends UNSUBSCRIBE. RefCount's restart-on-resubscribe semantics map
  exactly to GENA re-subscription.
- Renewal loop at `TIMEOUT/2` on `PeriodicTimer(options.TimeProvider)` - the
  `PortMappingLease` renewal loop is the proven template (same clock, same test approach).
- `UpnpEvent` is a small closed union (records): `PropertyChanged(Name, Value, Seq,
  IsInitialState)` | `Subscribed(Sid, Timeout)` | `RenewalFailed(Message)` | `Resubscribed` |
  `GapDetected(ExpectedSeq, ActualSeq)`. Per-item failure is data; `OnError` only when the
  subscription is unrecoverable and auto-recovery is off (Q2).
- SEQ tracking: 0 = initial full state (flagged); a gap → `GapDetected`, then (per Q2 default)
  automatic resubscribe → fresh SEQ 0 state, so consumers holding "last known value" per
  variable are consistent again. This mirrors the lease's Expired-then-recover semantics.
- Sync `Dispose` path (abrupt): no UNSUBSCRIBE; the device expires the subscription at
  TIMEOUT on its own - the same "finite lease makes abrupt safe" property as port mappings.

### 2.4 Integration

- `UpnpClientOptions` additions: `EventCallbackPort`, `EventSubscriptionTimeout`
  (requested `Second-n`, default 1800), `AutoResubscribe` (Q2).
- The listener is client-owned, shared, disposed with the client (both disposal paths).
- Namespace `UPnP.Rx.Eventing` for wire records; `Events()` lives on `UpnpService`.

## 3. Phases (one commit each, on dev/4.0.0; version bump to 4.0.0 in E1)

**Status 2026-07-24: E1-E6 implemented and committed (122 tests green, incl. real-socket
loopback integration tests). E7 (releases/4.0.0 branch, tag, publish) is deliberately NOT
executed - it waits for the author's code review and the §5 hardware validation, per
instruction. No pushes from this session.**

**Found in author validation (2026-07-24):** "Watch live" failed with SUBSCRIBE → HTTP 412
on every strict device (MIOS, which skips callback validation, was the only device that
accepted). Root cause: on macOS/Linux upstream reports the wildcard-bound SSDP socket
(`0.0.0.0:1900`) as the envelope's local endpoint, and we trusted it - the CALLBACK header
advertised `http://0.0.0.0:…`. Fixed at three layers (`LocalRoute` helper): wildcard is
normalized to "unknown" at the discovery boundary, eventing and port mapping fall back to a
routing-table lookup toward the device, and the loopback/discovery/port-mapping tests now
feed the wildcard envelope reality instead of a kind fake. Windows was never affected
(upstream binds the interface address there). Recorded as upstream issue candidate #3 in
the main plan §9 - since fixed by the author upstream (SimpleHttpListener.Rx 7.2.0,
packet-information based); UPnP.Rx now references it directly (main plan §8 item 7).

**Pre-release review (2026-07-24):** a four-dimension pass (bugs, smells, UDA 2.0
compliance, C# 14) is recorded in `upnp-rx-v4.0-code-review.md`; all findings implemented
same day (B1-B6, S1-S6, C1, M1-M3) - notable: stale-replay fix, SEQ wrap-to-1, engine
catch-all → OnError, graceful client `DisposeAsync` (UNSUBSCRIBE before teardown),
callback listener 405/500 answers.

| Phase | Deliverable |
|---|---|
| E1 | Version 4.0.0; `GenaComposer`/`GenaParser` + fixtures (incl. captured + malformed NOTIFYs) |
| E2 | Callback listener adapter + routing, behind the HotStart-style test seam |
| E3 | Subscription engine: Create/RefCount lifecycle, renewal on TimeProvider, SEQ/gap handling - FakeTimeProvider-tested |
| E4 | `UpnpService.Events()`, `UpnpClient` wiring, options; loopback integration test |
| E5 | Clause 4 compliance review against the UDA 2.0 text (incl. 412 handling, response deadlines) + fixes |
| E6 | Dashboard eventing hook (live state on expanded cards - the 4.0 backlog item) + README/samples |
| E7 | 4.0.0 release: releases/4.0.0 branch, tag, Trusted Publishing |

## 4. Decisions (author, 2026-07-24)

- **Q1 - API shape: observable-first only.** `Events()` with shared lifetime; no explicit
  handle type in 4.0 (addable later without breaking).
- **Q2 - recovery: auto-resubscribe**, surfaced as events (`RenewalFailed`/`Resubscribed`/
  `GapDetected`), `AutoResubscribe = true` default. The stream never dies from device flakiness.
- **Q3 - callback port: ephemeral default (0)**, fixed-port option for firewall rules;
  firewall note goes in Troubleshooting.
- **Q4 - LastChange typed helper: 4.1.** Core stays generic (values are strings; escaped
  AVTransport XML passes through verbatim); recorded in the 4.1 candidates list.
- **Q5 - late-subscriber replay: INCLUDED in 4.0** (author: "if replay is a good feature,
  include it - it should not be difficult with Rx" - correct). Design: the shared engine
  keeps a last-known-value snapshot per variable, maintained under the same gate that
  serializes emissions; a late subscriber receives the snapshot first (each event flagged
  `IsReplay = true`), then the live stream, with no gap and no duplicates - because both the
  snapshot read and the live attach happen under the gate. Consequence: sharing is a small
  hand-rolled ref-count over that gate rather than bare `Publish().RefCount()` (which cannot
  replay per-variable state).

## 4b. 4.1 pull-in review (author asked: does anything deferred belong in 4.0?)

Reviewed the full 4.1 list; verdict: **nothing structural moves**. The roster stays 4.1 - Q5's
snapshot gives eventing its own state cache without it, and pulling the roster in would double
4.0's surface. Self-healing stays an investigation. Typed SOAP helpers, `TryService`,
trimming audit: unrelated to eventing, stay. **One small rider folds in**: the dashboard
reconnect toast (`FluentToastProvider`) lands with E6, since E6 is already dashboard work -
near-zero marginal cost. Upstream issue filing remains independent of any release.

## 5. Author integration-test guide (the manual validation protocol)

E4 ships **`Sample.Eventing`** - a console that discovers devices, subscribes to a chosen
service and prints every `UpnpEvent` (colored: replay dim, changes white, lifecycle events
cyan/yellow). The protocol, step by step:

1. **Prep** (once): run on the host, not a container; on Windows pause SSDPSRV; expect a
   firewall prompt on first run (the callback listener) - allow it. Best test device: a Sonos
   speaker (exemplary GENA citizen).
2. **Happy path**: `dotnet run --project samples/Sample.Eventing` → pick a Sonos
   `AVTransport:1`. Expect: `Subscribed` with SID + timeout, an initial burst flagged
   `IsInitialState` (SEQ 0), then silence. Press play/pause/next on the speaker → a
   `PropertyChanged` per action within ~1 s (`LastChange` values are escaped XML - expected,
   typed parsing is 4.1).
3. **Replay (Q5)**: run a second `Sample.Eventing` against the same service (or use its
   `--second` flag) → the newcomer immediately prints the last-known state flagged as replay,
   then follows live.
4. **Renewal**: leave it running past half the timeout (default 1800 s → ~15 min; use
   `--timeout 120` for a 1-minute renewal cycle). Expect a renewal to pass silently (no event
   unless it fails).
5. **Recovery**: unplug the speaker's network for ~30 s mid-subscription, replug. Expect
   `RenewalFailed` events while unreachable, then `Resubscribed` + a fresh initial state.
6. **Graceful exit**: Ctrl+C → sample disposes → UNSUBSCRIBE; on the wire the device stops
   NOTIFYing immediately (verifiable by re-running: fresh SID).
7. **Abrupt exit**: `kill -9` the sample → no UNSUBSCRIBE; the device times the subscription
   out on its own at TIMEOUT - nothing leaks (the finite-lease property).
8. **IGD reality check**: subscribe to the gateway's `WANIPConnection` - expect
   `PortMappingNumberOfEntries`/`ConnectionStatus` events when mappings change (add one via
   the dashboard); IGDs event lazily, so tolerate delays - report what yours does.

Report format: which step, expected vs. observed, and the sample's log lines. Loopback
integration tests (CI-runnable) cover the same lifecycle against a scripted fake device;
this guide covers what only real hardware can prove.

## 6. Risks, honestly

- **Device quirk surface is the widest yet**: sloppy TIMEOUT formats, NOTIFYs before the
  SUBSCRIBE response returns (real Sonos behavior - the engine must buffer or tolerate
  unknown-SID NOTIFYs briefly), devices that never send SEQ, IGDs that event poorly. The
  leniency policy earns its keep here; fixtures from the author's real network are the best
  insurance.
- **Firewall/UX**: an inbound listener is a new failure mode for consumers (host firewalls
  prompt or block). Troubleshooting docs must grow a section; the dashboard's hint-panel
  pattern applies.
- **Container testing limits**: subscriptions to real devices can't run in CI; loopback +
  seam tests cover the engine, author covers reality - same split that worked for 3.0.
