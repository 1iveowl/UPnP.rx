# Rx + ReactiveUI deep review (2026-07-25, dev/4.0.0 @ f2895b2)

Scope, as requested by the author: (A) verify the house Rx rules against what Rx 7.0.0
actually is; (B) line-by-line Rx correctness and concurrency review of the library;
(C) the samples' Rx (server + WASM client); (D) ReactiveUI usage and version currency.
**Findings only - nothing below is implemented.** Each finding carries a verdict and a
proposed action; §F collects the upstream issues and reflections; §G is the ranked
implementation list awaiting author sign-off.

---

## A. The rules, verified against Rx 7.0.0 (all stand)

- **A1 - "TimeProvider is the one clock" stands, and is still necessary.**
  System.Reactive 7.0.0 (verified: the latest stable on nuget.org, no patch releases)
  has **no TimeProvider integration and no IAsyncDisposable integration** - unchanged
  from the pre-3.0 audit. The library actually exceeds the rule: `src/` contains **zero
  time-based Rx operators** - every delay/period/timeout runs on `PeriodicTimer`,
  `Task.Delay` or `CancellationTokenSource` overloads that take the options'
  `TimeProvider` directly. The `TimeProviderScheduler` sketched in plan §9 never became
  necessary and should stay unbuilt (build-on-first-need was the rule).
- **A2 - the TestScheduler ban stands.** With no time-based Rx operators in `src/`,
  `TestScheduler` would exercise nothing real; one `FakeTimeProvider` per test drives
  every renewal, retry, timeout and TTL deterministically. 131 tests comply.
- **A3 - the package reference is correct.** The long-trailed "System.Reactive becomes a
  facade over System.Reactive.Net" split has **not shipped**: `System.Reactive.Net` does
  not exist on nuget.org (checked 2026-07-25), and `System.Reactive` 7.0.0 is the
  go-forward package. No action.
- **A4 - the Rx 7 WASM scheduler defect is real and our mitigations are the right ones.**
  Rx 7.0.0's WASM enlightenment rejects the .NET 10 runtime (`WasmRuntime` type
  initializer: "does not support this version of the WebAssembly scheduler"), so any
  operator or ReactiveCommand that defaults into the platform scheduler dies on WASM.
  Client policy - explicit `CurrentThreadScheduler.Instance` as command output scheduler,
  explicit `DefaultScheduler.Instance` on `Observable.Timer` - is applied consistently
  (verified: both view models, `DeviceStreamClient`'s rescan fallback, no unscheduled
  time-based operator anywhere in the client). Already recorded as an upstream (dotnet/
  reactive) issue candidate in the 4.1 plan.
- **A5 - a load-bearing fact worth writing down: `System.Threading.Lock` is reentrant.**
  The engine relies on it: with the fake transport, `await` of already-completed tasks
  continues inline, so `Emit` re-enters `_gate` on the thread that already holds it
  inside `Subscribe`. Verified empirically on net10.0 today (and exercised by the test
  suite on every run). Proposed action: one comment on `_gate` naming the dependency
  (docs-only) - a future switch to a non-reentrant primitive would deadlock the tests.

## B. Library Rx findings

- **RX-1 (medium, the one real concurrency gap) - SEQ tracking is not under the gate.**
  `EventCallbackListener` handles requests via `SelectMany(request => FromAsync(...))` -
  unbounded inner concurrency - and `GenaSubscriptionSource.HandleNotify` reads/writes
  `SeqTracker.Expected` *before* taking `_gate`. Two NOTIFYs for the same subscription
  processed concurrently can race the expectation: false `GapDetected` → needless
  resubscribe, and the two NOTIFYs' property batches (each atomic under the gate) may
  interleave in arbitrary order. In practice a compliant device serializes per SID - it
  awaits our 200 before the next NOTIFY - so the window needs a misbehaving or
  duplicating sender. Still, the engine's own invariants shouldn't depend on device
  politeness. **Proposal:** move the SEQ check + expectation update inside the same
  `lock (_gate)` block that emits the batch (SeqTracker becomes gate-guarded state; a
  few lines). The heavier alternative - per-token serialization in the listener - is not
  warranted.
- **RX-2 (low, deliberate - document it) - observer callbacks run under `_gate`.**
  Replay-on-subscribe, live emissions, errors and shutdown completion all invoke
  `OnNext/OnError/OnCompleted` while holding the gate. This is what makes Q5's replay
  atomic (no gap, no duplicate) and mirrors what `Observable.Synchronize` itself does;
  reentrancy (dispose/subscribe from inside a handler on the same thread) is safe per
  A5. The real cost: a *blocking* observer stalls the engine and NOTIFY handling, and a
  handler that blocks waiting on another thread that needs the gate deadlocks.
  **Proposal:** keep the design; add one sentence to the `Events()` XML docs -
  "handlers should not block; do async work in the pipeline, not the subscriber"
  (which is house Rx rule 1 anyway). No code change.
- **RX-3 (low) - subscribing after client disposal yields a silent, dead stream.**
  `EventingContext.GetOrCreateSource` throws once `_disposed` is set, but a source
  handed out earlier accepts new subscribers after `ShutdownAsync`/lifetime
  cancellation: the fresh engine's linked token is born canceled, `RunAttemptsAsync`
  exits immediately, and the observer never hears anything - no error, no completion,
  forever. **Proposal:** in `Subscribe`, when `_clientLifetime.IsCancellationRequested`,
  complete (or error) the observer immediately instead of starting a dead engine.
- **RX-4 (low) - `PortMappingLease.Dispose` can race the renewal loop's emissions.**
  Sync `Dispose` calls `_events.OnCompleted()` while the loop may be mid-`OnNext` on
  another thread - a small Rx-grammar violation window (`Subject<T>` drops post-terminal
  `OnNext`s without corruption, but concurrent terminal+next is still outside the
  grammar). **Proposal:** wrap the subject once at construction with
  `Subject.Synchronize(_events)` and emit/complete through the synchronized view -
  grammar-correct for a two-line change. (`DisposeAsync` is already clean: it awaits the
  loop before completing.)
- **RX-5 (low) - `EventingContext.DisposeAsync` shuts sources down sequentially.**
  N live subscriptions worst-case N × `ActionTimeout` on the graceful path.
  **Proposal:** collect the `ShutdownAsync` tasks and `Task.WhenAll` them - goodbyes in
  parallel, same bounded ceiling.
- **RX-6 (info, sample) - the hub's event channel drops oldest silently.** The
  SignalR bridge caps at 64 with `DropOldest` - right shape for a live view, but a
  "no silent caps" purist would surface a dropped counter. Sample-grade: note only.

**Verified sound (checked explicitly, no action):** the Rx grammar of the hand-rolled
`GenaSubscriptionSource` (all notification paths serialize through `_gate`; errored
observers are removed before any later emission; completion and error are mutually
exclusive per observer) · `Observable.Create` async-subscribe patterns (subscription
established before side effects; dispose-on-faulted-subscribe both in `DiscoverDevices`
and the SHL-derived listener usage) · `Merge` of independently-serialized upstream
streams (Merge serializes its output) · per-subscription `Distinct` state and its
interplay with the shared RefCounted upstream · `SelectMany`+`FromAsync`+typed-`Catch`
per-item failure containment in `DiscoverDescribedDevices`, `PortMapper.DiscoverGateways`
and the dashboard pipelines · no `Subscribe(async …)` anywhere in the repo · no
`.Wait()`/`.Result` · hot/cold temperature documented on every public observable
(all six) · `ConfigureAwait(false)` discipline in `src/` (CA2007 as error) ·
the rescan generation-overlap (subscribe-before-dispose) against the RefCount teardown
race · subjects never abruptly disposed while producers may emit (house lesson applied
in `UpnpDiscoveryService`).

## C. Samples' Rx (server + WASM client)

- **RX-7 (low, docs) - `DeviceStreamClient`'s subjects rely on external serialization.**
  `BehaviorSubject`/`SourceCache` are mutated from SignalR callbacks; the SignalR client
  dispatches handlers sequentially per connection and Blazor WASM is single-threaded, so
  emissions are serialized *de facto*. Correct here - but the class is the sample people
  will copy into Blazor **Server**, where neither guarantee holds alone. **Proposal:**
  one comment stating the serialization assumption.
- Server-side pipelines (`UpnpDiscoveryService`, `GatewayService` lease forwarding, hub
  channel bridge): rule-compliant - async work in the pipeline, sync `Subscribe`
  handlers, per-item `Catch`, errors logged not thrown. No findings beyond RX-6.

## D. ReactiveUI

- **RUI-1 (verified) - version currency: we are on the latest stable.**
  `ReactiveUI.Blazor` 23.2.28 is the newest stable (checked 2026-07-25); 24.0 exists
  only as `beta.1..3`. **Proposal: stay put**; revisit at 24.0 stable and re-verify the
  builder API then (the 23 → 24 major may move `RxAppBuilder` surface again).
- **RUI-2 (verified) - bootstrap is the correct 23.x pattern.**
  `RxAppBuilder.CreateReactiveUIBuilder().WithBlazorWasm().BuildApp()` before the first
  `WhenAnyValue` (RxApp statics are gone in 23); transient view models paired with
  `ReactiveInjectableComponentBase<T>`, with the base-class disposal caveat handled
  explicitly and commented in both pages.
- **RUI-3 (low, recommend) - `ThrownExceptions` is not observed on any command.**
  Today no command *can* throw (every body catches and returns error strings), so this
  is latent, not live. But an unobserved ReactiveCommand exception takes down the
  default handler, and the invariant "bodies never throw" is one refactor away from
  false. **Proposal:** subscribe each command's `ThrownExceptions` and route to
  `LastError`/toast - three lines per view model, closes the class of failure.
- **RUI-4 (info, verified working) - OAPH scheduling on WASM.** `ToProperty` with no
  scheduler works on the dashboard today (it does not resolve into the broken Rx 7 WASM
  scheduler - if it did, the pages would blank out per A4). Optional consistency polish:
  pass the same explicit scheduler the commands use, so no ReactiveUI default is relied
  on anywhere. Cosmetic.
- **RUI-5 (verified, keep) - the "revision pump" is the right Blazor pattern.**
  Changeset → index → `ToProperty` → property notification → re-render looks unusual
  next to WPF-style `INotifyCollectionChanged` binding, but Blazor does not observe
  collection-change events at all - some property must pump renders. The alternatives
  (manual `Subscribe` + `StateHasChanged` in the component) trade a declarative pipeline
  for imperative wiring and gain nothing. `SortAndBind` (the modern DynamicData operator)
  and `SourceList.Edit` batching are both current best practice. DynamicData 9.4.33 is
  the latest stable.
- **RUI-6 (optional, author's call) - no `IActivatableViewModel`/`WhenActivated`.**
  The canonical ReactiveUI lifecycle pattern ties subscriptions to view activation; we
  use ctor-composed pipelines + `CompositeDisposable` + explicit page `Dispose`. For
  Blazor WASM with transient VMs this is defensible and simpler (activation adds
  ceremony and its Blazor story is weaker than XAML's), but it is a *deviation from
  canon* and should be a recorded decision either way. **Proposal:** record "ctor
  composition + explicit disposal, deliberately" in §8, or adopt `WhenActivated` in the
  two view models - not both.
- **RUI-7 (optional) - `ReactiveUI.SourceGenerators`** could replace the hand-written
  `RaiseAndSetIfChanged` properties (`[Reactive]`) and OAPH boilerplate
  (`[ObservableAsProperty]`). Modernization, not correctness; adds a package to the
  samples. Noted for the 4.1 CTM/R3 evaluation session, where it belongs.

## E. Concurrency model (reference for the findings above)

Threads that touch the eventing engine: SHL connection-loop threads (NOTIFY arrival →
`HandleNotify`), thread-pool continuations (engine awaits, `ConfigureAwait(false)`
throughout), timer callbacks (`PeriodicTimer` renewals), and subscriber threads
(`Subscribe`/dispose, plus inline continuations when awaited tasks are already
complete). Serialization map: every observer notification and all replay state
(`_lastKnown`, `_observers`) go through `_gate` (reentrant, A5); engine lifecycle state
(`_engineCts`, `_engineTask`) is gate-guarded; the route table is a
`ConcurrentDictionary`; **the one piece outside any guard is `SeqTracker` - RX-1.**
On the WASM client, everything runs on the single browser thread; on the server samples,
SignalR and Rx pipelines ride the thread pool with per-pipeline serialization from Rx
operators (`Synchronize` where sources are multi-threaded).

## F. Upstream issues and reflections (Rx-relevant; filing is the author's action)

**dotnet/reactive (Rx 7.0.0):**
- **File-worthy: the WASM scheduler enlightenment rejects .NET 10** (A4). Repro is
  minimal: any Blazor WASM app on net10.0 touching an operator that resolves the
  platform scheduler → `TypeInitializationException` from `WasmRuntime` ("does not
  support this version of the WebAssembly scheduler"). Workaround (explicit schedulers)
  is easy but undiscoverable - the failure presents as a blank page. Already on the 4.1
  upstream-candidates list; this review confirms it against 7.0.0 final.
- **Reflection: no `TimeProvider` bridge in Rx 7.** The BCL's one-clock abstraction and
  Rx's `IScheduler` world still don't meet - every TimeProvider-disciplined library
  must either hand-write an `IScheduler` adapter or avoid time-based operators entirely
  (we chose avoidance, A1). Worth voicing on the existing dotnet/reactive TimeProvider
  discussions rather than a fresh issue.
- Packaging: the `System.Reactive.Net` facade split never shipped (A3) - a fact to
  track across future Rx releases, not an issue.

**ReactiveUI (23.2.28):**
- **File-worthy: `WithBlazorWasm()` registers a scheduler that dies on .NET 10 + Rx 7.**
  ReactiveUI's WASM platform registration resolves into the same broken `WasmRuntime`
  path, which is why every `ReactiveCommand` here carries an explicit
  `outputScheduler` - the framework's own Blazor default is unusable on net10.0. This is
  the ReactiveUI-side twin of the dotnet/reactive issue above; whichever repo fixes
  first unblocks the other's default.
- Watch item: 24.0 is in beta; the 23 bootstrap (`RxAppBuilder`) surface may move again
  at the major. Re-verify at 24 stable before adopting (RUI-1).

**Author's own stack (consolidating the Rx-relevant items already in plan §9):**
- `SSDP.UPnP.PCL`/`SimpleHttpListener.Rx` **candidate #4** - the shared RefCounted
  streams don't survive dispose-then-immediately-resubscribe (teardown races restart;
  cancellation-induced `SocketException(89)` surfaces as `OnError` on the *new*
  subscription because SHL's accept loop treats only `OperationCanceledException` as a
  normal stop). Two fixes upstream: treat cancellation-`SocketException` as completion,
  and make the shared streams restart-tolerant. Until then, downstream restart code must
  overlap subscriptions (as the dashboard rescan now does).
- Candidate #1 (no `ConfigureAwait(false)` in SSDP.UPnP.PCL) and candidate #2
  (`Device` sync-`Dispose` → `IAsyncDisposable`) remain open.
- Candidate #3 (`LocalIpEndPoint` wildcard) - **resolved** by the author in
  SimpleHttpListener.Rx 7.2.0; UPnP.Rx references it directly and keeps its `LocalRoute`
  defense for older runtime graphs.

## G. Proposed implementation list (awaiting author sign-off)

| # | Finding | Change | Size |
|---|---|---|---|
| 1 | RX-1 | SEQ tracking under the gate | small, +1 test (concurrent NOTIFY race) |
| 2 | RX-3 | Subscribe-after-shutdown completes instead of going silent | small, +1 test |
| 3 | RX-4 | `Subject.Synchronize` around the lease event subject | tiny |
| 4 | RX-5 | Parallel goodbyes in `EventingContext.DisposeAsync` | tiny |
| 5 | RUI-3 | Observe `ThrownExceptions` on all three commands | small |
| 6 | A5 + RX-2 + RX-7 | Comments/docs: Lock reentrancy, non-blocking-handler note, WASM serialization assumption | docs only |
| 7 | RUI-6 | Record the lifecycle decision in §8 (or adopt WhenActivated) | author's call |

Explicitly proposed as **no action**: A1-A4 (rules and versions all check out), RX-6
(sample-grade cap), RUI-1 (stay on 23.2.28 until 24 stable), RUI-4 (working as-is),
RUI-5 (correct pattern), RUI-7 (defer to the 4.1 evaluation).
