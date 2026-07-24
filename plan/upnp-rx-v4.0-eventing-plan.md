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

| Phase | Deliverable |
|---|---|
| E1 | Version 4.0.0; `GenaComposer`/`GenaParser` + fixtures (incl. captured + malformed NOTIFYs) |
| E2 | Callback listener adapter + routing, behind the HotStart-style test seam |
| E3 | Subscription engine: Create/RefCount lifecycle, renewal on TimeProvider, SEQ/gap handling - FakeTimeProvider-tested |
| E4 | `UpnpService.Events()`, `UpnpClient` wiring, options; loopback integration test |
| E5 | Clause 4 compliance review against the UDA 2.0 text (incl. 412 handling, response deadlines) + fixes |
| E6 | Dashboard eventing hook (live state on expanded cards - the 4.0 backlog item) + README/samples |
| E7 | 4.0.0 release: releases/4.0.0 branch, tag, Trusted Publishing |

## 4. Open questions for the author (answer before E3)

- **Q1 - API shape**: observable-first only (`Events()` with RefCount lifetime, as designed),
  or *also* an explicit `Task<UpnpEventSubscription>` handle like `PortMappingLease`?
  Recommendation: observable-first only; the handle can be added later without breaking.
- **Q2 - recovery policy**: on renewal failure/expiry/SEQ gap, auto-resubscribe (surfaced as
  events, stream never dies) vs. terminate with `OnError`. Recommendation: auto, with
  `AutoResubscribe = true` default - consistent with pipelines-never-die and the lease.
- **Q3 - callback port**: ephemeral by default (0) with a fixed-port option for firewall
  rules, or fixed default? Recommendation: ephemeral default; document the firewall note.
- **Q4 - LastChange helper** (AVTransport/RenderingControl XML-in-XML): core 4.0 or 4.1?
  Recommendation: 4.1 - core stays generic; the dashboard demo can show raw values.
- **Q5 - initial-state replay for late Rx subscribers**: when a second consumer subscribes to
  an already-live `Events()`, replay the last-known variable values (Replay-per-variable) or
  deliver only new changes? Recommendation: new changes only in 4.0 (simple, honest);
  last-known-state cache pairs naturally with the 4.1 roster.

## 5. Risks, honestly

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
