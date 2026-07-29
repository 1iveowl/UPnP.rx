# UPnP.Rx 6.0.0 — analyzers, and the API changes that delete most of them

**Status: in progress on `dev/6.0.0`.** Q1-Q5 were answered by the author on 2026-07-29 and
the answers are folded in below (§13): the generator is **in** behind its byte-identity gate,
6.0.0 is agreed, the read-back honesty fix rides along, the raw-wire-log riders are **shelved**,
and UPNPRX003 stays at Warning.

**P1-P5 are done**, one commit each, build and tests green at every one (§10 has the table).
Two things found along the way are recorded where they belong rather than here: the migration
turned a previously-dropped SSDP message into a discovery-stream-killing throw (P1), and the
AOT verification in §11e is complete — with the diagnosis that section originally carried
corrected in place, because it was wrong.

Successor to the 5.0.0 honest-optionals release. Two themes, in this order:

1. **Adopt SSDP.UPnP.PCL 10.0.0 / SimpleHttpListener.Rx 7.6.0** — forced, wide, mechanical.
2. **Move this library's failure modes from run time to build time** — by API change first,
   analyzer second, generator third, nothing fourth.

The headline finding, measured rather than assumed: **of the seven candidate rules in the brief,
four are deleted before a line is written** — three because the API already made the bad state
unrepresentable, one because a tool already in this build catches it. That is the
SSDP.UPnP.PCL §0 result reproducing itself, and it is the most useful thing in this document.

---

## 1. What was measured before this plan was written

Everything in this section is a command that was run, not a belief. Where it contradicts the
brief, the brief is corrected here rather than 300 lines further down.

### 1a. Inherited-rule flow — verified at two hops, and across a ProjectReference

The brief says five rules "should already run over UPnP.rx source" and asks for two-hop
verification. Done, in a throwaway project referencing **only** `SSDP.UPnP.PCL 10.0.0`:

| Seeded violation | Analyzer's home | Hops | Fired? |
|---|---|---|---|
| `new MxSeconds(30)` | SSDP.UPnP.PCL | 1 | ✅ SSDP001 |
| `new DynamicPort(80)` | SSDP.UPnP.PCL | 1 | ✅ SSDP003 |
| `source.Subscribe(async x => …)` | SimpleHttpListener.Rx | **2** | ✅ SHLRX001 |

So **two-hop package-analyzer flow works**, despite `SSDP.UPnP.PCL.nuspec` recording
`<dependency id="SimpleHttpListener.Rx" version="7.6.0" exclude="Build,Analyzers" />`. That
`exclude` attribute is NuGet's serialization of the default `PrivateAssets`, and it did not stop
the flow. Do not reason from the nuspec here; the measurement is what counts.

**And across a ProjectReference edge.** A second, more consequential measurement: this repo's
samples and tests reach the library by `ProjectReference`, and a seeded `Subscribe(async …)` in
`samples/Sample.Browser` and in `tests/UPnP.Rx.Tests` **both fired SHLRX001**, sourced from a
package the *library* references. This sharpens pitfall 1 into two statements that are easy to
conflate:

- Analyzers delivered by a **package** flow through a ProjectReference edge. ✅ measured here.
- Analyzers delivered by an analyzer **project** (`OutputItemType="Analyzer"`) do not. — still
  the reason every in-repo consumer needs its own reference to our analyzer projects.

**Practical consequence:** once P1 lands, the samples and tests get SSDP001/003/005 and
SHLRX001/002 for free. They will *not* get `UPnP.Rx.Analyzers` without their own reference.

### 1b. `ExcludeAssets="analyzers"` still does not work — third independent measurement

With `ExcludeAssets="analyzers"` on the `SSDP.UPnP.PCL` reference, all three seeded violations
still fired (fresh `obj/`, fresh restore — pitfall 5 respected). The brief's "measured twice, in
two repos" is now measured three times. **Do not document it as an off-switch.** The working
escape hatches remain `.editorconfig` severity, `<NoWarn>`, and `#pragma warning disable`.

### 1c. A clean corpus run, with a seeded violation to prove it meant something

`SimpleHttpListener.Rx` was bumped 7.4.0 → 7.6.0 in a scratch copy of `HEAD` (it drops in cleanly
against SSDP.UPnP.PCL 9.1.0). Whole solution — library, five samples, tests — built:
**0 warnings, 0 errors.** SHLRX001 and SHLRX002 have **zero hits** on this codebase.

That result is worthless on its own, so violations were seeded in three places and all three
fired; then every file was restored and `cmp`-verified byte-identical (process rules 4, 5, 6).

**One consequence worth stating loudly:** `TreatWarningsAsErrors=true` is repo-wide, so an
inherited *warning* is a **build error** here. The seeded SHLRX001 in `src/` failed the build and
stopped the samples from compiling at all. Any real hit found during P1 blocks the release until
it is fixed or explicitly suppressed — which is the desired behaviour, but it must be expected
rather than discovered.

### 1d. The time model: no violations, because there is nothing to violate

The brief calls the time-model rule "most promising, because it is a documented house policy that
nothing currently enforces". Reading the code first, as instructed, changes that assessment
completely:

- **Zero time-based Rx operators exist** anywhere in `src/`, `samples/` or `tests/`. No
  `Throttle`, `Timeout`, `Delay`, `Buffer`, `Sample`, `Window`, `Observable.Timer`,
  `Observable.Interval`.
- **`TimeProviderScheduler` does not exist.** Project plan §5 rule 4 says to build it "in the
  first phase that uses a time-based operator — not speculatively". That phase never arrived.
- All temporal behaviour goes through `TimeProvider` primitives directly:
  `Task.Delay(ts, tp, ct)`, `new PeriodicTimer(period, tp)`, `new CancellationTokenSource(ts, tp)`,
  `tp.GetTimestamp()`/`GetElapsedTime()`.

The policy is currently satisfied by total abstinence. A rule here would guard against drift, not
report an existing mistake — and **the drift is already guardable with a tool this repo runs.**
See §7a: seven lines of `BannedSymbols.txt` do it, measured.

### 1e. Three candidate rules were already deleted by the existing API

| Brief's candidate | What the code actually says | Outcome |
|---|---|---|
| "Check whether `AddPortMappingAsync` takes raw `int`s — if so, a value type plus a rule" | Ports are already `ushort`, on all six port parameters and on `PortMappingEntry` | **Dropped.** `ushort` is the value type. Only `0` remains representable, and IGD vendors differ on whether `0` is a wildcard — a rule there cannot meet the zero-FP budget |
| "`Protocol` — if it is a `string` rather than an enum, that is an API change" | Already `enum Protocol { Tcp, Udp }` with a `ToWireString()` extension | **Dropped**, done in 3.0.0 |
| "`DescribedDevice.Service(string)` throwing … check whether a `TryService` pattern is warranted" | `TryService(string)` returning `UpnpService?` shipped in 4.1.0, beside `HasService` | **Dropped**, done |

### 1f. `PublicApiAnalyzers` is already adopted

Deliverable 6 of the brief ("adopt PublicApiAnalyzers, take the baseline before any API redesign")
landed in **4.2.0**. `src/UPnP.Rx/PublicAPI.Shipped.txt` is 747 lines; `Unshipped.txt` is empty.
The RS0016-harvest bootstrap is not needed. The work in this release is *rolling* the ledger for
the breaking changes, which is ordinary release hygiene here.

### 1g. Generator preconditions: the AOT motivation is absent, the safety motivation is present

Enumerating the brief's categories against this codebase:

| Run-time discovery a generator could precompute | Present? |
|---|---|
| Reflection | No — one `typeof(UpnpService).Assembly.GetName().Version` in the user-agent string |
| `Regex` compiled at first use | No — already `[GeneratedRegex]` (`XmlLeniency`) |
| Serializer graphs (`XmlSerializer`, `DataContractSerializer`, `JsonSerializer`) | No — LINQ to XML (`XDocument`/`XElement`) throughout, in all six parsing/composing files |
| DI scanning | No |
| Logging message templates | No — already `[LoggerMessage]` (5.0.0) |

So the AOT/trimming argument for a generator is **nil**, and confirmed by measurement: a native
AOT publish of `Sample.PortMapper` produced **zero ILC trim/AOT analysis warnings** and got all the
way to the native link step. (It fails there in this devcontainer only because ILC invokes the
linker with `--target=x86_64-linux-gnu` and the container has `gcc` but no `clang`. That is an
cross-compile on an arm64 host, not a library defect and not a missing toolchain — see §11e, where that diagnosis is corrected and the verification completed.)

What *is* present is the compile-time-safety argument, and it is concrete (§8).

### 1h. CI runs the test project by name — the exact trap the brief warns about

```yaml
- name: Test
  run: dotnet test tests/UPnP.Rx.Tests/UPnP.Rx.Tests.csproj --no-build -c Release
```

Adding `tests/UPnP.Rx.Analyzers.Tests` would leave it never run by CI, silently. This is the same
failure that hid 50 analyzer tests in SSDP.UPnP.PCL. Fixed in P2.

Note the plan's own §9 recorded a deliberate reason for naming the project ("run `dotnet test`
against the test csproj, not the slnx, once samples join the solution"). Since then `dotnet test`
on a `.slnx` handles non-test projects correctly; P2 must verify that in CI rather than assume it,
and fall back to naming both test projects explicitly if it does not.

### 1i. `plan/` is not gitignored

In this repo `plan/` is
**tracked** — `git ls-files plan/` lists eleven documents and the history has `Plan: …` commits.
This file follows the repo, not the brief.

---

## 2. Prerequisite: the SSDP.UPnP.PCL 10.0.0 migration

Verified against the shipped assembly (`10.0.0`, published 2026-07-29), not against the brief's
table. Deltas where the two disagree are marked ⚠.

| 9.1.0 | 10.0.0 | Our adoption sites |
|---|---|---|
| `MSearchResponseObservable()` → `MSearchResponse` | → `ReceivedMSearchResponse` | `UpnpClient.ToDiscovered`, `AnnouncementStream` |
| `NotifyObservable()` → `Notify` | → `ReceivedNotify` | `UpnpClient.NotifiesOf`, `ToDiscovered`, `RosterUpdates`, `DeviceLost` |
| `MSearchRequest` (concrete, `TransportType` flag) | `MSearchRequest` abstract; `MulticastMSearch` / `UnicastMSearch` | `UpnpClient.SendSearchesAsync` — the `TransportType = Multicast` line disappears into the type |
| `MSearchRequest.MX` as `TimeSpan` | `MulticastMSearch.MX` as `MxSeconds` (int; ≥ 1 enforced) | `SendSearchesAsync`; **forces a public API decision** — see §3a |
| — | ⚠ `MulticastMSearch.MX` **defaults to 1 s**, not 3 | We always pass MX explicitly, so no behaviour change; worth a test that pins it |
| — | ⚠ `SendCount` defaults to 2, values < 1 treated as 1 | No rule needed, no change |
| `BOOTID` / `CONFIGID` / `NEXTBOOTID` nullable | unchanged | none |
| — | ⚠ `Received*.SEARCHPORT` is **`int?`**, not `DynamicPort?` (deliberate: lenient-in). `Notify`/`MSearchResponse` (send) use `DynamicPort?` | Not surfaced by us; verify no regression |
| `IControlPoint.ParseFailures()`, `ControlPoint.CaptureRawMessages`, `RawMessage`, `IsUuidUpnp2Compliant`, `ResponseReason`, `NLS` — **all already present** | unchanged, only carried onto the new `Received*` types | ⚠ **Correction:** an earlier revision of this table listed these as new in 10.0.0. They are not — all six shipped in **9.1.0** and were verified against both assemblies. Nothing here is a migration cost, and nothing here is gated on 10.0.0. See §9 |
| `USN.EntityType` | gains `Unknown` (=6) | `USN` flows through `DiscoveredDevice` untouched; **more messages now reach us**, some with `EntityType.Unknown`. `SearchTargets`/dedup must be checked for an exhaustive switch |
| `IsUpnp2` | removed; use `SupportsAtLeast(major, minor)` | Not used anywhere (grep-verified) — 5.0.0 already avoided it |
| `DeviceInfo.UpnpMajorVersion/MinorVersion` `int?` | unchanged | `UpnpVersionClaims` already treats them as `int?` |

**Blast radius:** 14 files touch SSDP types (8 in `src/`, 6 in `tests/`). No sample touches them
directly. `SearchTargets`, `UpnpVersionClaims` and `Model/ParseResult` need no change beyond
recompilation.

`SimpleHttpListener.Rx` also moves **7.4.0 → 7.6.0** (the brief says 7.5.0; 7.6.0 is current and is
what SSDP.UPnP.PCL 10.0.0 depends on). Measured to drop in cleanly on its own (§1c).

**This is P1, its own commit, green, before any analyzer work.**

---

## 3. The candidate → outcome table

The four outcomes, in the order §0 demands: **API change → analyzer → generator → nothing.**

| # | Candidate | Kind of constraint | Outcome | Reasoning |
|---|---|---|---|---|
| 1 | M-SEARCH `MX` at our surface (`DiscoverDevices`, `SearchAsync`, `UpnpClientOptions.DefaultMx`) | range | **API change** — `TimeSpan?` → `MxSeconds?` | The ≥ 1 floor becomes a type invariant; the ≤ 5 advisory is then reported by **SSDP001 on the consumer's own literal**. Upstream designed for exactly this: `MxSeconds`' own docs say the ceiling "is left to the `SSDP001` analyzer". **Rule deleted.** |
| 2 | `UpnpClientOptions.EventCallbackPort` (`int`, 0 = ephemeral) | range | **API change** — `int` → `ushort` | Deletes the 1–65535 half outright; 0 stays legal and meaningful. **Rule deleted.** |
| 3 | Port-mapping ports | range | **Nothing** | Already `ushort` (§1e) |
| 4 | `Protocol` | structural | **Nothing** | Already an enum (§1e) |
| 5 | `DescribedDevice.Service` throwing | structural | **Nothing** | `TryService` shipped in 4.1.0 (§1e) |
| 6 | Time-based Rx operator without an explicit scheduler | house policy | **Nothing new** — `BannedSymbols.txt` | Measured working, zero analyzer code (§7a). Shipping it would fire on ordinary consumer Rx and blow the FP budget |
| 7 | **Port-mapping lease duration** outside IGD's 0–604800 s | range | **API change + analyzer UPNPRX001** | Survives any type change, per §0. Today a *negative* lease silently becomes a **permanent** mapping (§4a) |
| 8 | **`UpnpClientOptions` values** outside their documented ranges | range | **API change + analyzer UPNPRX002** | Same shape; direct analogue of SSDP005 at our surface (§4b) |
| 9 | **The lease returned by `AddPortMappingAsync` is discarded** | resource | **Analyzer UPNPRX003** | Measured: CA2000 does **not** cover it (§4c) |
| 10 | Sync `using` on an indefinite (`TimeSpan.Zero`) lease | resource | **Dropped** | The abrupt path is documented as legitimate; separating deliberate from mistaken needs the lease argument from another call site in general. Cannot meet zero FP (§7c) |
| 11 | `UpnpService.InvokeAsync` called with arguments that cannot be right for the action | structural | **Generator** (gated) | This *is* the generator question, not a rule (§8) |
| 12 | Relative-URL resolution against `DeviceDescription.Location` | — | **Dropped** | Entirely internal to `DescriptionParser`; nothing crosses the public surface for a rule to see |
| 13 | Disposal model (`DisposeAsync` graceful / `Dispose` abrupt) | — | Partly #9, otherwise **dropped** | The enforceable part is "the lease was never disposed at all". The rest is a documented, deliberate choice |

**Score: three API changes, three analyzers, one gated generator, six dropped.** Five of the six
drops were decided by reading the code, not by writing anything.

---

## 4. The rules that survive

**False-positive budget, stated before measuring** (unchanged from both prior projects):
**zero false positives; a high false-negative rate is accepted by design.** Literals and
compile-time constants only. No dataflow, no cross-method inference, no "probably". A rule people
learn to suppress poisons the quiet rules shipped beside it.

**Severity: Warning for all three**, justified per rule below. Not Error — that forces `#pragma`
into code which is correct against a specific device. Not Info/Hidden — an invisible rule protects
nobody. Note §1c: under this repo's `TreatWarningsAsErrors`, Warning *is* Error locally.

### 4a. UPNPRX001 — port-mapping lease duration outside IGD's range

**What it catches.** A literal or constant `lease` argument to `AddPortMappingAsync`,
`AddAnyPortMappingAsync` or `PortMapper.MapPortAsync` that is negative, or above 604 800 seconds
(7 days).

**Why it matters — this is the strongest case in the release.** `InternetGateway.AddArguments`
composes the wire value as:

```csharp
["NewLeaseDuration"] = ((uint)mapping.LeaseDuration.TotalSeconds).ToString()
```

.NET's floating-point → integer conversion has been **saturating** since .NET Core 3.0, so
`(uint)(-5.0)` is `0`. And `0` is IGD's encoding for **an indefinite mapping**. Therefore:

> Asking for a *negative* lease today silently creates a **permanent** port forward on the
> router — the opposite of what was asked for, with no exception, no log line, and no event.

`TimeSpan.MaxValue` saturates the other way, to `uint.MaxValue`. A 30-day lease composes
`2592000`, which the standardized service template rejects — the checked-in
`wanipconnection1_scpd.xml` fixture declares `PortMappingLeaseDuration` as `ui4` with
`allowedValueRange` `0`–`604800`, so the range is not folklore, it is in the repo.

**Accompanying API change (the part that must land first).** Validate in
`InternetGateway.AddAsync` and on `PortMappingEntry.LeaseDuration`: negative or > 604 800 throws
`ArgumentOutOfRangeException` at the call, instead of composing a lie. Per §0 the rule *survives*
this — `TimeSpan.FromDays(30)` still throws at run time — which is precisely why the analyzer
earns its place on top.

There is a related honesty bug in the same code, found while reading, which should ride along
(§6b): the *read-back* path maps an unparsable `NewLeaseDuration` to `TimeSpan.Zero`, i.e. to
"indefinite". Same absent-vs-zero conflation 5.0.0 existed to remove, one layer down.

**Prior art.** None. IGD's lease range is domain-specific; nothing in the SDK, Meziantou,
Roslynator, ErrorProne or the Rx ecosystem knows about it. The nearest relative is our own
sibling's SSDP005 ("a device configuration value outside its UDA 2.0 range"), same shape,
different specification.

**Severity: Warning.** Not Error: a specific router may accept a longer lease, and the fixture's
range comes from the standardized template rather than from any guarantee about the device in
front of you. Not Info: the negative case is silent and permanent.

**Code fix.** Yes, and safe: clamp to the boundary (`604800` / `TimeSpan.Zero` for the negative
case is *not* safe — it means "permanent", which is the bug). The offered fix for a negative lease
must be `TimeSpan.FromSeconds(604800)`? No — the honest fix is to surface both options as separate
fixes: "use the maximum lease (7 days)" and "make this mapping explicitly indefinite
(`TimeSpan.Zero`)", so the second is a decision the author makes rather than one the fix makes for
them. For the over-max case, one fix: clamp to `604800`.

### 4b. UPNPRX002 — a `UpnpClientOptions` value outside its documented range

**What it catches.** A literal or constant in a `UpnpClientOptions` object initializer (or a
`with` expression) that is provably nonsense:

| Property | Invalid | Why |
|---|---|---|
| `EventSubscriptionTimeout` | ≤ 0, or < 1 second | `GenaHeaders.ComposeTimeout` emits `$"Second-{(long)t.TotalSeconds}"`. A negative value composes `Second--5`; a sub-second value composes `Second-0`. Both are malformed GENA (UDA 2.0 clause 4.1.2) |
| `DescriptionTimeout`, `ActionTimeout` | ≤ 0 | A non-positive `CancellationTokenSource` timeout cancels immediately; every description fetch and every SOAP call fails instantly |
| `RosterExpiryFallback` | ≤ 0 | Every device without a usable `max-age` expires from the roster on arrival |

Deliberately **not** included: "unusually short but positive". `ActionTimeout = 100 ms` may be
exactly right on a fast LAN. The rule fires only on values that cannot be right anywhere.

**Accompanying API change.** Validate in the record's `init` accessors, so the throw lands at the
initializer. `DefaultMx` leaves this list entirely — it becomes `MxSeconds?` (§3, #1) and
`EventCallbackPort` becomes `ushort` (§3, #2). Both are examples of the API change deleting the
rule rather than needing it.

**Prior art.** None for these properties. Generic "argument must be positive" analyzers do not
exist in the SDK; `CA1508`/`CA2208` are about different things. SSDP005 is the shape precedent.

**Severity: Warning**, same reasoning as 4a.

**Code fix.** No. There is no defensible default to substitute — the right value is the author's
intent, and guessing it would be worse than the diagnostic.

### 4c. UPNPRX003 — the port-mapping lease is discarded

**What it catches.** `await gateway.AddPortMappingAsync(…)` (or `AddAnyPortMappingAsync`, or
`PortMapper.MapPortAsync`) as a bare expression statement, or assigned to a discard `_`.

**Why it matters.** The returned `PortMappingLease` owns three things: the mapping on the router,
a live renewal loop, and — for the `PortMapper` one-liner — the whole discovery chain including a
`UpnpClient`. Discarding it means the mapping is never deleted, the renewal `Task` runs for the
life of the process, and in the one-liner case a `UpnpClient` and its sockets leak with it. The fix
is one word: `await using`.

**Prior art — checked, and this is the interesting part.** The obvious candidate is **CA2000**
("Dispose objects before losing scope"). It was measured, not assumed:

- Enabled repo-wide, CA2000 reports **28 hits, all in `tests/`, zero in `src/` and zero in
  `samples/`** — the classic ownership-transfer pattern in test setup.
- With CA2000 live in a sample project (proved by a control seed: a discarded `new HttpClient()`
  fired immediately), a **discarded `await gw.AddPortMappingAsync(…)` did not fire.** CA2000's
  "created by" analysis tracks object creations and a few known factories, not an arbitrary
  interface method's return value.

So CA2000 does not cover this, and the rule has a job. JetBrains' `[MustDisposeResource]` covers
the shape but requires ReSharper/Rider, does not run in `dotnet build` or CI, and cannot ship in
our package.

**Free win found on the way:** because `src/` and `samples/` are already CA2000-clean, this repo
can enable `dotnet_diagnostic.CA2000.severity = warning` for `src/` and `samples/` at **zero
cost**, leaving it `none` in `tests/` — exactly the split the repo already uses for CA2007. Ten
minutes of work, no new code. Included in P3.

**Severity: Warning.** Not Error: a short-lived CLI that deliberately leaves a permanent mapping
behind and exits is a real, if rare, pattern, and one `#pragma` with a comment is the right way to
say so.

**Code fix.** Yes, and safe: rewrite to `await using var lease = await …;`. Emit any type name
fully qualified with `Simplifier.Annotation` (pitfall 10).

---

## 5. Rule metadata

| | UPNPRX001 | UPNPRX002 | UPNPRX003 |
|---|---|---|---|
| Category | `Usage` | `Usage` | `Reliability` |
| Severity | Warning | Warning | Warning |
| Help link | `…/UPnP.rx#upnprx001` | `#upnprx002` | `#upnprx003` |
| Code fix | 2 fixes (clamp / explicit indefinite) | none | 1 fix (`await using`) |
| Triggers on | literal + constant only | literal + constant only | syntactic |
| Expected corpus hits | 0 | 0 | 0 |

A zero-hit expectation is the design, not a disappointment — these are guards against a mistake
the current code does not make. The corpus run is still mandatory (§11c), and still meaningless
without a seeded violation.

---

## 6. API changes, collected

### 6a. Breaking (these make it 6.0.0)

1. `IUpnpClient.DiscoverDevices(ST?, TimeSpan?)` → `(ST?, MxSeconds?)`; same for
   `DiscoverDescribedDevices` and `SearchAsync`; `UpnpClientOptions.DefaultMx` `TimeSpan` →
   `MxSeconds`.
   *Migration hazard to document loudly:* `mx: TimeSpan.FromMilliseconds(500)` today truncates to
   `0` seconds on the wire; under `MxSeconds` it becomes a compile error. That is the point, but
   it must be in the release notes, not discovered.
2. `UpnpClientOptions.EventCallbackPort` `int` → `ushort`.
3. Range validation added to `UpnpClientOptions` init accessors and to the lease parameters —
   behaviourally breaking for code that currently passes nonsense and gets nonsense.
4. The `SSDP.UPnP.PCL` reference moves to a new major, so consumers must move with it.

### 6b. Non-breaking, riding along (the absent-vs-zero read-back)

`InternetGateway` maps an unparsable or missing wire value to a sentinel that means something
specific and wrong:

```csharp
LeaseDuration = uint.TryParse(entry["NewLeaseDuration"], out var s) ? TimeSpan.FromSeconds(s) : TimeSpan.Zero  // "indefinite"
ExternalPort  = ushort.TryParse(entry["NewExternalPort"],  out var e) ? e : (ushort)0
InternalPort  = ushort.TryParse(entry["NewInternalPort"],  out var i) ? i : (ushort)0
Uptime        = uint.TryParse(result["NewUptime"],         out var u) ? TimeSpan.FromSeconds(u) : TimeSpan.Zero
```

A gateway that omits `NewLeaseDuration`, or returns something unparsable, is reported as having a
mapping that **never expires**. This is the same conflation 5.0.0 existed to remove, one layer
down, and house leniency rule 4 ("unparsable optional fields stay unset") says it should be
`TimeSpan?`/`ushort?`. Making `PortMappingEntry`'s properties nullable is breaking — but the
release is already breaking, and process rule 10 says measure before deferring: this is four
property type changes, four call sites, and their tests.

**Decided: included** (Q3, author 2026-07-29). It is small, it is the same theme as the release
before it, and pre-release an aligned break is free while later it is not.

### 6c. Deliberately *not* changed

`InternetGateway` does not call `Scpd.ValidateAndOrderArguments`, even though that method already
performs SCPD-driven validation of exactly these arguments (including `allowedValueRange`). Wiring
it in would make every `AddPortMapping` fetch the SCPD first — network work on the hot path. This
is the seam the generator would close at compile time instead (§8), so it stays as-is pending Q1.

---

## 7. What is already enforced — the "nothing" outcomes, with evidence

### 7a. Time model rule 3, via `BannedSymbols.txt` (measured)

Seven lines appended to the existing `BannedSymbols.txt`, e.g.

```
M:System.Reactive.Linq.Observable.Throttle``1(System.IObservable{``0},System.TimeSpan);Time model rule 3: pass an explicit scheduler over the component's TimeProvider
```

Measured result: `s.Throttle(TimeSpan.FromSeconds(1))` fails the build with **RS0030**, while
`s.Throttle(TimeSpan.FromSeconds(1), scheduler)` on the very next line does **not**. Banning is
per-overload and exact — which is the whole rule, expressed in the mechanism this repo already
uses for time-model rule 1.

**Why not ship it as a package analyzer.** The policy is about *our* components sharing one clock.
A consumer writing `client.Roster().Throttle(TimeSpan.FromSeconds(1))` in their own app is not
making our mistake, and firing there is noise that would blow the zero-FP budget and teach people
to suppress our whole prefix. Repo-local is not a lesser answer here; it is the correct scope.

Covered overloads: `Throttle`, `Timeout`, `Delay`, `DelaySubscription`, `Buffer`, `Sample`,
`Window`, `Observable.Timer`, `Observable.Interval` — the scheduler-less overloads only.

**Related:** `TimeProviderScheduler` is still not needed and should still not be built
speculatively (plan §5 rule 4). The banned-symbol entry is what makes "we need one" impossible to
miss on the day someone reaches for a time-based operator.

### 7b. CA2000 for `src/` and `samples/` (measured: 0 hits)

See §4c. One `.editorconfig` line, zero cost, adjacent coverage for free.

### 7c. Why the sync-`using`-on-an-indefinite-lease rule was dropped

`Dispose` on a lease is a documented, correct choice — the docs say so, and the safety argument is
that a *finite* lease expires on the router by itself. The genuinely wrong case is narrower: sync
`using` on a lease created with `lease: TimeSpan.Zero`, where nothing expires and nothing deletes.
Catching that requires the lease argument, which in general lives at a different call site. A rule
restricted to the single-expression form (`using var l = await gw.AddPortMappingAsync(…, TimeSpan.Zero);`)
would be sound but would catch almost nothing while implying it catches the class. UPNPRX003
covers the more common and more damaging shape.

---

## 8. The generator question

### 8a. The case, on this codebase's own evidence

The brief is right that UPnP.Rx is different from SSDP.UPnP.PCL here, and the evidence is stronger
than the brief assumes:

- **`InternetGateway` is a hand-written SCPD wrapper.** Seven magic-string action names
  (`GetExternalIPAddress`, `GetStatusInfo`, `GetSpecificPortMappingEntry`, `DeletePortMapping`,
  `GetGenericPortMappingEntry`, `AddAnyPortMapping`, `AddPortMapping`) and roughly twenty
  magic-string argument/out-argument names (`NewExternalPort`, `NewLeaseDuration`,
  `NewReservedPort`, …). Every one is validated only by a SOAP fault from a device.
- **A second wrapper already exists in embryo.** `samples/…/DeviceNode.razor` hard-codes
  `"SetVolume"`, `"SetMute"`, `"DesiredMute"` against `RenderingControl`. The brief asks "if a
  second such wrapper is ever wanted" — it is already being written by hand.
- **The schema-to-code mapping is already implemented, at run time.**
  `ScpdExtensions.ValidateAndOrderArguments` checks the action exists, that every in-argument is
  present, that no unknown argument is passed, and that each value satisfies its state variable's
  `dataType`, `allowedValueList` and `allowedValueRange`. A generator does not invent this
  mapping; it moves an existing, tested one from run time to build time — and removes the network
  fetch that made it too expensive for `InternetGateway` to use (§6c).
- **The ranges are already checked in.** `tests/…/Fixtures/wanipconnection1_scpd.xml` is the
  schema a generator would read.

The case *against* is unchanged and real: a checked-in SCPD is the published template, and a real
device may differ. **A generator that emits a wrapper for a device that does not implement it
produces confident, wrong code.**

### 8b. Decision (Q1: in)

**Yes, narrowly, and gated on a byte-identity proof.**

- **Source: a checked-in `.scpd.xml` supplied as an `AdditionalFiles` item**, not a marker
  attribute with schemas embedded in the generator. The first is honest about where the schema
  came from; the second hides provenance behind convenience, and provenance is exactly what is at
  stake when device and template disagree.
- **What it emits:** a `partial` typed façade over the *same* `IUpnpService.InvokeAsync` — one
  method per action, typed parameters from `dataType`, a small `record` per action's
  out-arguments, and the `allowedValueRange` checks baked in as constants. It calls the existing
  runtime path; it does not become a second protocol implementation.
- **What it must document, in the generated XML docs themselves:** that the wrapper asserts what
  the *template* declares, not what the device in front of you implements, and that a device may
  still answer with a SOAP fault.
- **Adoption gate.** Generate `WanIpConnection` and prove it composes **byte-identical SOAP
  envelopes and SOAPACTION headers** to today's hand-written `InternetGateway`, for every one of
  the seven actions, over a matrix of inputs (process rule 8). **Only if that harness is green
  does `InternetGateway`'s body move to the generated wrapper in this release.** If it is not
  green, the generator still ships and is used by a sample and by tests, and `InternetGateway`
  adopts it in 6.1 — with the reason recorded.

### 8c. Mechanics (guidance to verify, not settled — none of this was exercised in the siblings)

- `IIncrementalGenerator` only. No `ISymbol`, `Compilation` or syntax node in the pipeline's data
  model; project to small equatable records immediately. A cacheability assertion is part of the
  test suite, not an afterthought.
- Testing is **snapshot-based** (Verify). The analyzer rules keep their
  `Microsoft.CodeAnalysis.Testing` verifier tests. Do not force one style onto both.
- The marker attribute, if any, is **ordinary public API declared in UPnP.Rx** — the consumer
  already references the library at run time. Do not generate it.
- Generated code must be clean under `TreatWarningsAsErrors` **and** under UPNPRX001–003 shipping
  beside it. `<auto-generated/>` headers; and an explicit test that our own rules do not fire on
  our own output.
- **A generated member is public API with no deprecation path.** It goes in the
  `PublicAPI.Shipped.txt` ledger like anything else, and renaming one is a breaking change.

### 8d. What a generator would *not* fix

Nothing about AOT or trimming (§1g). If the XML layer ever drifts from LINQ to XML to
`XmlSerializer`/`DataContractSerializer`, that changes — and a P9 check should confirm it has not.

---

## 9. The raw wire log — measured, then shelved (Q4: **out**)

**Correction to an earlier revision of this document, which had it wrong twice.** This section
previously claimed that `ControlPoint.CaptureRawMessages`, `RawMessage` and
`IControlPoint.ParseFailures()` were *new in 10.0.0*, and that surfacing them was a rider the
migration unlocks. Both halves are false, verified against the 9.1.0 and 10.0.0 assemblies:

- **All of it shipped in 9.1.0** — the version this repo builds against *today*. So does
  `SsdpParseFailure` (with `RawMessage` and `RawMessageText()`), `IsUuidUpnp2Compliant`,
  `ResponseReason` and `NLS`. 10.0.0 only carries them onto the new `Received*` types.
- **The seam is already built and already dead.** `FakeControlPoint` implements `ParseFailures()`
  and owns a `Failures` subject — added to satisfy the interface, and **used by no test**
  (grep-verified). Nothing in `src/` or `samples/` mentions `ParseFailures`, `CaptureRawMessages`
  or `RawMessage`.

So this was never gated on the migration. It is available now, was available in 5.0.0, and
deferring it is a pure scope choice rather than a dependency wait.

**Cost, measured rather than guessed (process rule 10).**

| Part | Work | Lines |
|---|---|---|
| A — surface `ParseFailures()` on `IUpnpClient`/`UpnpClient` | One `Observable.Defer` + disposed guard, shaped exactly like `Announcements()`; 2 ledger lines; 2-3 tests against the fake seam that already exists | ~60 with tests |
| B — raw capture | `UpnpClientOptions.CaptureRawMessages`; wire it to the owned `ControlPoint`; add `RawMessage` to `Announcement`; thread it through the four `AnnouncementStream` projections and the byebye branch (which today goes via `DeviceLost()` and drops the bytes) | ~120 with tests |

**Decision: out of 6.0.0 (author, 2026-07-29).** The raw wire log remains upstream territory,
which is what `UpnpClient.Announcements()` already says — and that sentence is **not** stale, as an
earlier revision of this section wrongly asserted. It delegates raw bytes to the control point, and
upstream does now provide them there. It stands as written; no P1 doc change is owed.

Three notes for whoever picks this up later:

- **Neither part is forced into a major** *if* `Announcement.RawMessage` lands as an init-only
  property rather than a sixth positional parameter. A positional parameter would change the
  compiler-generated `Deconstruct` and break consumers' positional patterns; an init-only property
  does not. Choosing the property shape keeps this a free minor, permanently.
- **Part B is cheapest during a phase that is already rewriting `AnnouncementStream`'s
  projections** — which P1 is. Doing it later means touching those five projections twice. That is
  the one real argument for pulling it in, and it was weighed and declined.
- **Upstream candidate:** `CaptureRawMessages` sits on `ControlPoint`, not on `IControlPoint`, so a
  client holding the interface (our bring-your-own-control-point constructor, and every consumer
  who mocks the seam) cannot turn capture on through it. If Part B is ever built, that gap needs
  filing against SSDP.UPnP.PCL rather than working around downstream.

---

## 10. Build phases (one commit each, build + tests green at every commit)

| Phase | Work |
|---|---|
| **P1** | **Migration.** SSDP.UPnP.PCL 10.0.0 + SimpleHttpListener.Rx 7.6.0. `Received*` types, `MulticastMSearch`, `MxSeconds`. `EntityType.Unknown` handled. API ledger rolled. No new features — and specifically no raw-wire-log surfacing (§9, Q4). |
| **P2** | **Inherited-rule verification + CI.** Seed and remove one violation of each of SHLRX001/002 and SSDP001/003/005 in library, sample and test projects; record the hit list. Fix CI to test the solution (§1h). *(Release-tracking files moved to P4 — RS2008 only applies to a project that defines analyzers, and none exists until then.)* |
| **P3** | **API changes that delete rules.** `mx` → `MxSeconds?`; `EventCallbackPort` → `ushort`; range validation on options and lease; §6b read-back honesty; `BannedSymbols.txt` time-operator entries (§7a); CA2000 for `src/`+`samples/` (§7b). Ledger rolled. |
| **P4** | **Analyzer infrastructure.** Three projects (§12), pinned versions in `Directory.Build.props`, bundled packaging, `OutputItemType="Analyzer"` references in library **and** every in-repo consumer, `PublishAot` condition, `AnalyzerReleases.{Shipped,Unshipped}.md`. Nothing implemented yet — this phase is green with zero rules. |
| **P5** | **UPNPRX001** + two code fixes + verifier tests + mutation battery. |
| **P6** | **UPNPRX002** + verifier tests + mutation battery. |
| **P7** | **UPNPRX003** + code fix + verifier tests + mutation battery. |
| **P8** | **Generator** (Q1: in): pipeline, snapshot tests, cacheability check, byte-identity harness (§8b), adoption decision recorded either way. |
| **P9** | **Docs + release verification.** README per-rule docs under stable anchors (explicit `<a id>` anchors - a GitHub heading slug is the whole heading text, so `### UPNPRX001 - title` would anchor as `#upnprx001---title`); packed-`.nupkg`-off-a-local-feed consumer verification; CI native-AOT publish **and run** on the runner's own RID; version bump; CODEMAP; version history. *Both ledger rolls (`PublicAPI` and `AnalyzerReleases` Unshipped → Shipped) move to P10: rolling freezes the surface, and P10 is allowed to change it.* |
| **P10** | **Code review** (§11h): adversarial bug hunt, code smells, dedup — then the fixes, in the same phase. Last, because it reviews everything the release added. |

---

## 11. Testing and process

Process rules 2–11 from the brief, mapped onto this release. Where one does not apply, that is
stated rather than quietly skipped.

### 11a. Mutation battery (process rule 3)

Every rule's tests must be proved capable of failing: break each branch of each analyzer in turn
and confirm the corresponding tests go red. Watch specifically for the two kinds SSDP.UPnP.PCL
found:

- **Redundant defences.** In UPNPRX002 every property check must be isolated — a test for
  "negative `ActionTimeout`" must leave every *other* property valid, or a different guard fires
  first and the test passes with its own subject deleted.
- **Passing for the wrong reason.** Assert on the diagnostic **message and location**, not just
  the ID. Three SSDP.UPnP.PCL tests asserted an exception type that a *different* throw produced
  first.

### 11b. Seed-application assertion (process rule 4)

Every mutation script asserts `old in source` before rewriting, and asserts byte-identical
restoration with `cmp` afterwards (process rule 5). Both were honoured in the measurements behind
§1 and both must be honoured again — the §1c corpus run restored three files and `cmp`-verified
each one.

### 11c. Corpus run (process rule 6)

Report the raw hit list **before any tuning**. Expected: zero for all three rules (§5). Mandatory
companion: a seeded violation of each, watched firing through the real chain — and then through a
consumer project that takes the packed `.nupkg` off a local feed, which is the only configuration
that tests what a real user gets. The in-repo `ProjectReference` path does **not** exercise it
(§1a).

### 11d. Byte-identity (process rule 8) — applies twice

1. **M-SEARCH composition across the migration.** Compose the discovery datagram from identical
   `UpnpClientOptions` under 9.1.0 and under 10.0.0 and diff the bytes. A difference is either an
   upstream change to record or our bug; either way it must be seen, not assumed. (We cannot hold
   *upstream's* composer identical — only the inputs we hand it.)
2. **SOAP envelopes, if the generator adopts** (§8b). Non-negotiable gate on P8.

### 11e. AOT verification — **done**, and the earlier diagnosis here was wrong

**Correction.** An earlier revision of this section said the native link failed because ILC
passes `--target=x86_64-linux-gnu` and the devcontainer has `gcc` but no `clang`, and
prescribed installing clang. The missing toolchain was not the problem: **the container is
`aarch64`, and the publish had asked for `-r linux-x64`** — a cross-compile, which the native
`gcc` rightly refuses. Nothing was wrong with the container.

Published for the host RID it works, **with `gcc` alone**:

- `dotnet publish samples/Sample.PortMapper -c Release -r linux-arm64 --self-contained
  -p:PublishAot=true` → a 9.5 MB native binary, **zero ILC trim/AOT warnings**.
- **Run**, which is the half that counts (deliverable 9 says publishing *and* running, not ILC
  staying quiet): it exercises discovery, the SSDP socket path and `LocalRoute`, and exits
  cleanly with the documented "no gateway answered" message — multicast does not work in a
  container, so that is the correct outcome rather than a failure.
- Re-verified after installing clang: identical result, still zero warnings.

`clang` and `zlib1g-dev` are installed by `.devcontainer/post-create.sh` anyway, because clang
is what Microsoft documents as the prerequisite and what ILC prefers when present. That is
parity, not a fix — recorded plainly so nobody later reads it as one.

**What P9 still owes:** the AOT smoke *in CI*, so a regression is caught without anyone
remembering to run it. `ubuntu-latest` is x64, so that step must use the runner's own RID —
hard-coding one that does not match the host is the whole trap above. And re-confirm the XML
layer has not drifted to a serializer (§8d).

### 11f. Differential fuzz (process rule 7) — not applicable, and here is why

No parser rewrite is planned. `DescriptionParser`, `ScpdParser`, `SoapParser`, `SoapComposer`,
`GenaParser` and `LastChangeParser` touch no SSDP types and are untouched by the migration
(grep-verified: the SSDP dependency reaches 8 files in `src/`, none of them in `Parsing/` except
`ParseResult.cs`, which is a copy). If P1 turns out to need a parser change, this rule switches
back on and the fuzz harness is built.

### 11g. Adversarial bug hunt (process rule 9)

Runs in **P10** (§11h), **after** the release "looks done", and is not skipped because the inline
reviews found things. In SSDP.UPnP.PCL skipping it hid a fourth instance of that release's
recurring bug plus four inert tests.

### 11h. P10 — the code review, and then the fixes

A three-pass review of everything this release added, run **last** so it sees the finished shape
rather than an intermediate one. Findings are implemented in the same phase; only genuine design
questions come back to the author. Each pass has its own failure mode to hunt, drawn from what the
previous releases' reviews actually caught:

**Pass 1 — bug hunt (adversarial).** Not a re-read; an attempt to break it. Priority targets, in
order of how much this release disturbed them:

- **The migration's silent-miss class.** 5.0.0's near-miss was `uuid:x` vs bare `x` — a comparison
  that compiles, runs, and never matches. The `Received*` split touches every discovery projection,
  so every identity comparison, dedup key and cache key gets re-derived and re-checked against a
  test that would fail loudly if it stopped matching.
- **`EntityType.Unknown`.** More messages now reach us than before. Hunt for switches and
  `Where` filters that silently drop them, and for anywhere `Unknown` flows into a dedup key.
- **The lease path.** The negative-lease saturation bug (§4a) is the release's reason for existing;
  check its siblings — every `(uint)`/`(ushort)` cast over a wire value, every `TryParse`-to-
  sentinel, every place a range is asserted in one layer and assumed in another.
- **Analyzer and generator hosts.** Analyzers run on every keystroke: hunt for per-invocation
  allocation, `ISymbol`/`Compilation` captured in a generator pipeline's data model (defeats
  caching, keeps compilations alive), and any rule that reads syntax it has not null-checked.
- **Cancellation and disposal**, the recurring bug class in 4.x and 5.0.0 alike (a deadlock cycle,
  two cancellation bugs, then a stale cancellation poisoning the next engine run).

**Pass 2 — code smells.** Sentinels standing in for absence (the theme 5.0.0 and §6b exist to
remove); guards asserted twice in different layers with different answers; comments that describe
code that has since moved; `internal` seams that exist only for one test; XML docs making claims
the code no longer honours — with §9's correction as the standing example of how that happens.

**Pass 3 — dedup.** This repo has a specific, settled shape for this (`EngineSource`,
`TimedExchange`, `TestKit`; "per-item Rx pipeline shape is idiom, not duplication" —
[DECISIONS.md](DECISIONS.md), 2026-07-26). Apply the same standard to what 6.0.0 adds: the three
analyzers will share range-checking, literal/constant extraction, and diagnostic-property
plumbing, and the generator's tests will share fixture loading. Extract those; leave idiom alone.

**Verification, non-negotiable.** Every fix lands with a test that fails without it. The mutation
battery (§11a) is re-run afterwards, because a fix that changes a branch can quietly turn a live
test inert — which is precisely how SSDP.UPnP.PCL ended up with four tests that could not fail.

### 11i. Real network (process rule 11)

The author's manual pre-tag step, on real hardware: the port-mapping path against a real IGD
(including a deliberately over-long lease, to see what the router actually does with it — the rule
says the template forbids it, the device is the authority on what happens), and discovery against
the mixed UPnP 1.0/1.1/2.0 population that produced the Platinum and Vera findings.

---

## 12. Infrastructure (settled from the siblings — copy, do not re-derive)

Three projects, because RS1038 forbids an analyzer assembly referencing the Workspaces layer:

- `src/UPnP.Rx.Analyzers` — `Microsoft.CodeAnalysis.CSharp`
- `src/UPnP.Rx.Analyzers.CodeFixes` — `Microsoft.CodeAnalysis.CSharp.Workspaces`
- `tests/UPnP.Rx.Analyzers.Tests` — `net10.0`

Analyzer projects: `netstandard2.0`, `IsPackable=false`, `EnforceExtendedAnalyzerRules=true`,
`Nullable=annotations`. The two analyzer assemblies cannot reference each other; share
`DiagnosticIds.cs` via a linked `<Compile Include="…" Link="…" />`.

Versions pinned in `Directory.Build.props` (drift between the three is a real breakage):

| Package | Version |
|---|---|
| `Microsoft.CodeAnalysis.CSharp` | 4.8.0 (never newer than the host compiler — CS8032) |
| `Microsoft.CodeAnalysis.Analyzers` | 3.11.0 |
| `Microsoft.CodeAnalysis.CSharp.Analyzer.Testing` / `.CodeFix.Testing` | 1.1.3, `DefaultVerifier` |
| `Microsoft.CodeAnalysis.PublicApiAnalyzers` | 5.6.0 — **already referenced at this version** in `UPnP.Rx.csproj` |

Packaging: bundled into `UPnP.Rx.nupkg` under `analyzers/dotnet/cs`, never a separate package with
a dependency edge (that produces LimitedFunctionality in Visual Studio). `PublishAot` condition on
the `ProjectReference` items (pitfall 6). Release tracking from day one, with `Shipped.md`
containing **comments only** — a bare `### Shipped` header fails RS2007.

Pitfalls that apply here specifically: the test project needs an explicit modern
`Microsoft.CodeAnalysis.CSharp.Workspaces` reference (NU1701 under `TreatWarningsAsErrors`);
reference assemblies top out at `Net.Net90`, so the API under test is a **source stub** guarded by
a reflection test asserting our type names and range constants against the real types (this repo is
net10.0-only, so the stub is unavoidable); `netstandard2.0` has no `System.Index`; hoist the
per-diagnostic `ImmutableDictionary` to a static field; `{|RULEID:code|}` markup, not hand-computed
spans; `WellKnownFixAllProviders` (plural).

---

## 13. Decisions (answered by the author, 2026-07-29)

- **Q1 — the generator: IN**, narrowly, behind the byte-identity gate (§8b). It is the largest and
  riskiest single piece of the release, so the gate is what keeps that risk bounded: if the
  generated `WanIpConnection` wrapper does not compose byte-identical SOAP envelopes and
  SOAPACTION headers to today's hand-written `InternetGateway` across all seven actions, the
  generator still ships but `InternetGateway` does not adopt it in 6.0.0, and the reason is
  recorded. **Adoption is a measurement, not a decision made under momentum.**
- **Q2 — 6.0.0: AGREED.** The upstream major forces a major regardless; the three deliberate
  breaks in §6a ride it rather than waiting for an unrelated future one.
- **Q3 — the §6b read-back honesty fix: IN.** `PortMappingEntry.LeaseDuration` and the port
  properties become nullable, so a gateway's silence stops reading as "this mapping never expires".
- **Q4 — the raw wire log: OUT**, shelved (§9). A raw wire log remains upstream territory, exactly
  as `Announcements()` already documents. Measured cost for the record: ~60 lines for
  `ParseFailures` surfacing, ~120 for raw capture — and it was never gated on 10.0.0, since all of
  it shipped in 9.1.0.
- **Q5 — UPNPRX003 severity: Warning**, per the settled policy.

---

## 14. Risks

- **The migration is wide.** 14 files, and the `Received*` split touches the discovery hot path.
  Mitigated by P1 being mechanical, its own commit, and green — and by the M-SEARCH byte-identity
  harness (§11d).
- **An inherited rule may fire on real code during P1** and, under `TreatWarningsAsErrors`, block
  the build. The corpus is clean today for SHLRX001/002 (§1c) but SSDP001/003/005 have never run
  over this repo — they arrive with P1. Budget time for hits; do not suppress them to get green.
- **The generator is public API with no deprecation path**, and it is now in (Q1). Hold every
  generated member to at least the bar of the hand-written surface; it enters the
  `PublicAPI.Shipped.txt` ledger like anything else. The adoption gate (§8b) is the mitigation:
  `InternetGateway` moves to generated code only on a green byte-identity harness.
- **A zero-hit corpus can look like a broken analyzer.** Mitigated only by the seeded-violation
  proof through the packed package (§11c). A silent analyzer and a correct one look identical from
  a passing build.
- ~~**Devcontainer cannot complete a native AOT link.**~~ **Retired** (§11e): it can, and does.
  The residual risk is narrower and worth keeping — an AOT regression is only caught by a CI
  step that publishes for the *runner's* architecture, which P9 owes.

---

## 15. Out of scope

Raw SSDP wire log — `ParseFailures()` surfacing and raw capture — shelved with its cost measured
and its shape pre-decided (§9, Q4); multicast eventing; surfacing
`ReceivedNotify.IsUuidUpnp2Compliant` / `ReceivedMSearchResponse.ResponseReason`; any new
analyzer aimed at consumer Rx style; `TimeProviderScheduler` (still speculative — plan §5 rule 4);
dashboard features.
