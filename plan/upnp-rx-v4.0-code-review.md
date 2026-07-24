# UPnP.Rx 4.0 pre-release code review (2026-07-24, on dev/4.0.0)

Scope: full pass over `src/UPnP.Rx` in four dimensions - bugs, code smells, UDA 2.0
compliance, C# 14 modernity. The eventing layer (new in 4.0) was re-reviewed line by line;
parsers, SOAP, discovery and port mapping (adversarially reviewed pre-3.0) were re-read and
spot-verified. Findings are numbered B (bug), S (smell), C (compliance), M (modernization);
each carries its disposition. Implementation lands in the commit following this document.

## Bugs

- **B1 (medium) - stale replay to the first subscriber of a restarted engine.**
  `GenaSubscriptionSource.Subscribe` replays `_lastKnown` *before* the first-subscriber
  branch clears it, so the subscriber that restarts a stopped engine receives the previous
  run's state as replay - stale by definition (the device evolved while unsubscribed), and
  immediately superseded by the fresh SEQ-0 initial state. Q5's replay is for *late*
  subscribers; a first subscriber has nothing to catch up on. **Fix:** branch on
  "first" before replaying - fresh start clears, late subscriber replays.
- **B2 (low, also C) - SEQ wraps to 1, not 0 (UDA 2.0 §4.2.3).** The event key wraps from
  4294967295 to 1 (0 is reserved for the initial state). `Expected = actual + 1` overflows
  to 0, so a spec-compliant wrap triggers a false `GapDetected` and a needless resubscribe.
  **Fix:** wrap the expectation to 1.
- **B3 (medium) - an unexpected engine exception kills the stream silently.**
  `RunEngineAsync` handles `UpnpException` and cancellation, but anything else (an observer
  throwing in `OnNext` during an engine-context `Emit`, an unexpected transport failure)
  faults the engine task unobserved: no `OnError`, no log, subscribers wait forever.
  **Fix:** catch-all around the engine loop - log + `OnError` (source death is the one
  legitimate `OnError`, Rx rule 6).
- **B4 (low) - engine CTS leak on the error path.** After `Error(...)` ends the engine
  (AutoResubscribe off), `_engineCts` stays behind; the next first subscriber overwrites it
  without disposal. **Fix:** dispose the leftover CTS when starting a fresh engine.
- **B5 (medium, disposal model) - client `DisposeAsync` is not yet the graceful path for
  eventing.** It delegates to sync `Dispose`, which tears down the `HttpClient` while
  engines are mid-goodbye - live GENA subscriptions get no reliable UNSUBSCRIBE, and the
  class docs still say "v2 eventing will unsubscribe here". **Fix:** `EventingContext`
  becomes `IAsyncDisposable`: shut each source down (cancel engine, await its teardown -
  the UNSUBSCRIBE is already bounded by `ActionTimeout`), complete remaining observers,
  then release the listener; `UpnpClient.DisposeAsync` awaits it *before* disposing the
  `HttpClient`. Sync `Dispose` stays abrupt by design (finite timeouts make that safe).
  Docs updated to match reality.
- **B6 (low, HTTP citizenship) - unanswered callback connections.** The callback listener
  ignores non-NOTIFY requests entirely and, when a NOTIFY handler crashes, sends no
  response - either way the sender waits for a timeout. **Fix:** 405 (Allow: NOTIFY) for
  other methods, 500 when handling fails; both keep the stream alive.

## Code smells

- **S1** - `catch (Exception e) when (e is UpnpException)` instead of
  `catch (UpnpException e)` (2× in `GenaSubscriptionSource`). Simplified.
- **S2** - `_retryDelay` declared mid-file between methods. Moved to the fields.
- **S3** - fully-qualified `System.Net.IPAddress` / `Eventing.EventingContext` sprinkled
  through `UpnpService`, `DescribedDevice`, `UpnpClient` instead of usings. Cleaned.
- **S4** - `SimpleHttpListener.Rx` is consumed directly (listener, `HttpSender`) but only
  referenced transitively via SSDP.UPnP.PCL. A directly-used package must be a direct
  reference - and the direct reference is also how consumers get the 7.2.0
  `LocalEndPoint`/packet-info fix as a floor. **Author-approved dependency addition**
  (2026-07-24); recorded in §8 of the main plan.
- **S5** - `_engineTask` was write-only. Now consumed by the graceful shutdown (B5).
- **S6** - `FetchDescriptionAsync` lacks the disposed-client `OperationCanceledException` →
  `ObjectDisposedException` translation its sibling `FetchScpdAsync` has. Aligned.

## UDA 2.0 compliance (beyond the signed-off clause 2/3 review and the clause 4 doc)

- **C1** = B2 (SEQ wrap to 1). Fixed.
- Verified clean against the text, no action: SUBSCRIBE carries NT/CALLBACK/TIMEOUT and
  renewals/UNSUBSCRIBE carry SID only (§4.1.2/4.1.3); NOTIFY answered 200 immediately
  (§4.3.3, error table already enforced per the clause-4 review); empty property set
  treated as keep-alive; resubscribe-after-gap says goodbye to the old SID first (engine
  teardown order); requested TIMEOUT default 1800 s ≥ spec recommendation; SOAPACTION and
  charset quoting per §3.2.1; `XDocument.Parse` prohibits DTDs (no XXE surface).
  Multicast eventing (§4.3.4+) remains explicitly out of scope.

## C# 14 / modernity

- **M1** - `GenaSubscriptionSource._gate` was `object` + `Monitor`; now `System.Threading.Lock`
  (the rest of the codebase already uses it).
- **M2** - `ScpdExtensions` rewritten as a C# 14 `extension` block (same emitted static
  method, nicer declaration site).
- **M3** - `SoapComposer` null-argument iteration aligned with `ScpdExtensions`'
  `ReadOnlyDictionary<,>.Empty` idiom.
- Already modern throughout (no action): collection expressions, primary constructors,
  pattern switches, `GeneratedRegex`, `FrozenDictionary`, records with `with`, `Lock`
  elsewhere, `TimeProvider` everywhere.

## Verified clean (re-read this pass, nothing to report)

`DescriptionParser`, `ScpdParser`, `SoapParser`, `SoapComposer`, `XmlLeniency` (regexes
anchored, total), `GenaParser`/`GenaHeaders`, `UpnpClientOptions`, `PortMapper`,
`PortMappingLease` (disposal races correctly reasoned), `InternetGateway`,
`ScpdExtensions` (logic), description cache TTL/eviction logic, `LocalRoute`.

## Test additions accompanying the fixes

Engine restart must not replay stale state (B1); SEQ 4294967295 → 1 is not a gap (B2);
an observer throwing mid-emission surfaces as `OnError`, not silence (B3); graceful
shutdown UNSUBSCRIBEs and completes observers (B5); non-NOTIFY → 405 and crashing
handler → 500 on the callback listener (B6).
