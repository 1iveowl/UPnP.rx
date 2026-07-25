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

---

## Concurrency addendum (2026-07-25): the lock inventory, and whether Rx can replace it

Author question: `Lock` keeps appearing - is it really necessary, or can Rx (or another
technique) work around it? Assessment below; **no changes made pending author review**.

### The honest premise first

Rx does not eliminate locks - it *relocates* them. `Observable.Synchronize` is a lock
around notifications; `Subject<T>`, `ReplaySubject<T>`, `Publish().RefCount()` and
DynamicData's caches all serialize internally with their own gates. So the real question
per site is never "lock vs. no lock" but **"whose lock: a hidden, proven, composable one
inside an Rx primitive - or a small visible one of ours?"** The house practice that falls
out of this review: *stream-shaped* concerns (serializing emissions, fan-in from multiple
threads) belong to Rx's serialization operators; *lifecycle-shaped* concerns (create-once,
start-once, ownership handover) get the smallest visible `Lock`; the same state is never
guarded by both. C# structurally guarantees the worst lock sin can't happen here: `await`
inside a `lock` block does not compile, so no lock in this codebase is ever held across
asynchronous I/O.

### The inventory (all of it)

Four `Lock` objects in `src/` (~3.9k LOC), zero in the dashboard samples:

| Site | Concern | Shape |
|---|---|---|
| `GenaSubscriptionSource._gate` | replay atomicity + notification serialization + engine lifecycle + SEQ state | mixed (see below) |
| `UpnpService._scpdLock` | SCPD task cache, retry-on-fault | lifecycle |
| `UpnpClient._startLock` | control point start-once (upstream allows one `Start`) | lifecycle |
| `EventingContext._listenerLock` | listener + transport create-once | lifecycle |

Plus, already lock-free or Rx-serialized: `Interlocked` disposal flags (client, lease),
`Subject.Synchronize` on the lease events (RX-4), `.Synchronize()` + generation overlap in
the dashboard's rescan (which *replaced* a `Lock` with the Rx idiom when the author first
raised this), immutable records everywhere else.

### Site-by-site: the alternatives, costed

- **`_startLock`, `_listenerLock`, `_scpdLock` - keep.** All three are create/start-once
  guards, three lines each, uncontended by design. The standard "lock-free" replacements
  are `Lazy<T>`/`LazyInitializer` - which take the *same* lock internally
  (`ExecutionAndPublication`), just invisibly. The SCPD cache additionally requires
  retry-on-fault (only success is cached), which `Lazy<Task<T>>` cannot express and an Rx
  formulation (`FromAsync(...).Replay(1).RefCount()`) actively breaks: `ReplaySubject`
  caches `OnError` terminally, so one transient fetch failure would poison the service
  forever - rebuilding the connectable on fault lands right back at a CAS loop or a lock.
  Nothing stream-shaped here; Rx has no purchase. (One honesty note: `_listenerLock`
  covers the TCP socket bind - a one-time local syscall, acceptable, but it is the only
  lock in the codebase covering any syscall.)
- **`GenaSubscriptionSource._gate` - keep, and it deserves the full argument.** The gate
  does four jobs: (1) serialize notifications (the Rx grammar), (2) make Q5's
  late-subscriber replay atomic - snapshot read and observer attach must exclude
  concurrent emission or replay gains a gap/duplicate window, (3) first-subscriber-starts
  / last-dispose-stops engine ownership, (4) SEQ expectation + batch contiguity (RX-1).
  The Rx-native candidates, each evaluated:
  - `Publish().RefCount()` was the *original* design (plan §2.3) and was explicitly
    displaced by the author's Q5 decision (plan §4): RefCount gives sharing and
    restart-on-resubscribe, but has no per-variable replay - Q5 is why the hand-rolled
    ref-count exists at all.
  - `ReplaySubject` replays the last *N events*, not the last value *per variable*, and
    its terminal error is permanent (no engine restart). Wrong semantics twice over.
  - DynamicData's `SourceCache<PropertyChange, string>` genuinely does per-key
    state-then-deltas atomically - but it would (a) add a library dependency in violation
    of the §5 dependency lock, (b) reshape the API from the `UpnpEvent` union (with
    lifecycle events interleaved in order, and the `IsReplay` flag) into changesets,
    losing decided semantics, and (c) serialize with *its* internal locks - the critical
    section does not disappear, it moves behind someone else's API plus adapter code.
  - `Observable.Synchronize` over a raw subject serializes emissions (job 1) but does
    nothing for jobs 2-4 - the atomic snapshot+attach and the engine ownership would
    still need a critical section binding them to the emission gate. Same lock, plus a
    subject.
  The irreducible core: *some* mutual exclusion must bind "read the snapshot and attach
  the observer" to "emit and update the snapshot" - that is what per-variable replay
  **means**. Ours is one reentrant, documented (A5/RX-2), test-exercised gate with only
  O(observers) work and zero I/O under it. Every alternative keeps an equivalent critical
  section and adds indirection, a dependency, or a semantic regression.

### Upstream leverage (author question, 2026-07-25): could upstream changes remove locks?

Verified against the upstream sources, lock by lock:

- **`_startLock` - YES: this one is upstream-caused and would vanish.**
  `ControlPoint.Start` (SSDP.UPnP.PCL) guards itself with an unsynchronized `IsStarted`
  bool and *throws* on a second call - two racing callers either double-start the socket
  subscriptions or one takes an `SSDPException`. Our lock exists solely to compensate.
  Upstream fix, two flavors: (a) minimal - make `Start` idempotent and thread-safe (an
  `Interlocked` gate that turns the second call into a no-op); (b) idiomatic - drop
  explicit `Start` entirely and let the shared observables start lazily on first
  subscription (`Defer` inside, the same discipline UPnP.Rx applies one layer up). Either
  way `UpnpClient.EnsureStarted` and `_startLock` are deleted, and flavor (b) removes the
  start-once concept from the whole stack. **Recorded as upstream issue candidate #5.**
- **`_listenerLock` - no.** The laziness it guards is ours (no socket bind, no firewall
  prompt until eventing is first used) and the ephemeral port must be known synchronously
  to compose CALLBACK URLs - a lazily-binding upstream listener observable can't hand the
  port over before subscription. The alternatives (bind eagerly at client construction, or
  `Lazy<T>` with its identical hidden lock) are worse or equal.
- **`_scpdLock` - no.** Pure client-side caching of a plain HTTP fetch; no upstream
  library is even involved.
- **`_gate` - no, and no upstream API could absorb it.** Its critical section binds *our*
  subscribers' snapshot+attach to *our* emissions (Q5 replay atomicity) - upstream sits on
  the far side of the transport and never touches either. Housing a "per-key replay
  subject" in a shared upstream package would relocate the lock into the author's other
  repo, not remove it; that trades visibility for distance.

One adjacent elegance win while in upstream territory: fixing candidate #4 (RefCounted
streams surviving dispose-then-resubscribe) would delete the dashboard's
generation-overlap workaround - not a lock, but the same family of bespoke concurrency
reasoning that upstream shape currently forces downstream.

On "locks feel like a hack": the primitive itself is what Rx is built from - every
`Synchronize`, subject and RefCount holds one. The smell worth banning is *ad-hoc shared
mutable state with unnamed invariants*; each surviving lock here guards one named
invariant with documented scope, and the count is now trending down - four today, three
after upstream candidate #5 ships.

### Verdict

All four locks stay for now (one pending its upstream fix); each is the smallest honest
expression of a lifecycle or
replay-atomicity concern that Rx's primitives either cannot express (per-variable replay,
retry-on-fault caching) or would re-implement with their own hidden locks. Where the
concern really is stream-shaped, this codebase already reaches for the Rx tool -
`Subject.Synchronize`, `.Synchronize()`, generation overlap - and the dashboard now
contains no `Lock` at all. Recommendation: adopt the shape rule above ("streams get Rx
serialization, lifecycles get the smallest visible Lock, never both for one piece of
state") as the recorded house answer to "when is a lock acceptable", and change no code.
