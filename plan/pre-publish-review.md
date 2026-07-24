# Pre-publish code review (2026-07-24)

Independent adversarial review of `src/` + `tests/` before the first release push, complementing the
UDA compliance review. 15 findings; the confirmed high/medium defects were fixed the same day
(commit referenced below), the rest are recorded here with dispositions.

## Fixed

| # | Sev | Defect | Fix |
|---|---|---|---|
| 1 | High | `ParseActionResponse` threw `ArgumentException` on duplicate/case-variant out-argument names — the "total" parser wasn't | Group by name, first occurrence wins; regression test |
| 2 | High | Negative caching: a failed DDD fetch was cached forever per Location+ConfigId; same for a failed SCPD fetch per service | Failure evicts the cache entry (client) / faulted task is replaced on next call (service); regression tests |
| 3 | High | `Observable.Create` async-subscribe leaked the inner subscription to the shared RefCount SSDP streams whenever the subscribe task faulted or was cancelled mid-search | Dispose-on-failure around `SendSearchesAsync` |
| 4 | High | Unbounded `deviceList` recursion: a hostile ~2 MB document with deep nesting killed the process via `StackOverflowException` | Depth cap (16) truncates deeper nesting; parser stays total; regression test |
| 5 | Med | Renewal loop treated *any* `OperationCanceledException` as disposal — disposing the owning `UpnpClient` under a live lease stopped renewals silently | OCE filter now requires `_cts.IsCancellationRequested`; external cancellation surfaces as `RenewalFailed` |
| 6 | Med | Sync `Dispose` disposed the Subject/CTS while the renewal loop could still emit → `ObjectDisposedException` on an unobserved task | Abrupt path cancels + `OnCompleted` only; loop/GC handle the rest |
| 7 | Med | Relative `URLBase` became `file:///…` on Unix, poisoning every resolved URL and escaping as `NotSupportedException` from HttpClient | Base and resolved URLs must be absolute http/https; regression test |
| 8 | Med | `GetPortMappingsAsync` looped forever (and overflowed `int`) on a gateway answering every index | Capped at 65 535 entries |
| 9 | Med | Disposal not idempotent/thread-safe (`bool` guards); double-dispose could throw from `Dispose` | `Interlocked.Exchange` guards on client and lease |
| 10 | Med | Owned `HttpClient` kept its 100 s wall-clock `Timeout` — a hidden second clock capping `ActionTimeout`/`DescriptionTimeout` | Owned clients get `Timeout.InfiniteTimeSpan` (all timeouts are TimeProvider-driven) |
| 12 | Low | `DescribedDevice.Services` allocated a fresh list per property access | Built once in the constructor |
| 13 | Low | Any body containing a `UPnPError` element was classified a fault, even an HTTP-200 success response | `ParseFault` requires an actual `Fault` element (sloppy-nesting search kept within); regression test |

## Dispositions of the remaining findings (all addressed same day, follow-up commit)

- **11 (Med):** structural fix stays with the v1.1 roster (plan §8 decision 4); the
  long-subscription dedup-state caveat is now in `DiscoverDevices`' XML remarks.
- **14 (Low): fixed** — a disposed owning `UpnpClient` now surfaces as
  `ObjectDisposedException` from `InvokeAsync`/`GetScpdAsync`, not a bare
  `OperationCanceledException`.
- **15 (Low): fixed** — `IUpnpService`, `IInternetGateway`, `IPortMappingLease` extracted;
  concrete classes keep their concrete surfaces, the interfaces exist for consumer test
  doubles.

## Post-review additions (same follow-up commit)

From the product-gap assessment, all approved by the author: `UpnpClient.DiscoverDescribedDevices`
(discovery + description in one stream, UDN-deduplicated), `InternetGateway.GetSpecificPortMappingEntryAsync`
(null on 714) and `GetStatusInfoAsync` (typed `ConnectionStatusInfo`),
`Scpd.ValidateAndOrderArguments` (SCPD-driven argument marshalling — closes plan §4's "typed
argument marshalling" as validated string marshalling), README (try-it-in-two-minutes,
comparison table, troubleshooting) and diagnosing samples.

## Also verified in this pass

- `PublishTrimmed=true` on `Sample.PortMapper` (linux-x64): publishes with **zero IL trim
  warnings** across UPnP.Rx and the 1iveowl dependency chain.
- 66 tests green after all fixes.
