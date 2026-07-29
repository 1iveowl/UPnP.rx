# CODEMAP.md — UPnP.Rx

Repo map + phase status. **Update this file in every phase commit** (status table + tree). The full phase definitions live in [plan §7](plan/upnp-rx-project-plan.md).

## Phase status

| Phase | Deliverable (short) | Status |
|---|---|---|
| 0 | Repo infrastructure (props, editorconfig, slnx, CI skeleton, test project) | ✅ done |
| 1 | Model records, `ParseResult<T>`, `DescriptionParser` + fixtures | ✅ done |
| 2 | `ScpdParser`, `SoapComposer`/`SoapParser` | ✅ done |
| 3 | Edge: `UpnpClient` / `DiscoveredDevice` / `DescribedDevice` / `UpnpService` | ✅ done |
| 4 | `UPnP.Rx.PortMapping` + `PortMappingLease` + `Sample.PortMapper` | ✅ done |
| 5 | UDA 2.0 clause 2/3 compliance review + fixes | ✅ done |
| 6 | Packaging, README, samples polish, CI publish job | ✅ done |
| 7 | v3.0.0 release (branch, tag, Trusted Publishing) | ✅ released |
| E1-E6 | 4.0.0 GENA eventing: wire layer, callback listener, subscription engine, `Events()`, clause-4 review, dashboard live events (on `dev/4.0.0`) | ✅ done |
| — | 4.0 pre-release code review (`plan/upnp-rx-v4.0-code-review.md`) + fixes; SimpleHttpListener.Rx 7.2.0 direct ref; 126 tests | ✅ done |
| E7 | 4.0.0 release (branch, tag, Trusted Publishing + GitHub release) | ✅ released |
| R1 | 4.1.0 roster: `RosterChange` union, expiry on TimeProvider, replay, Q2 self-heal (on `dev/4.1.0`) | ✅ done |
| R2-R5 | Dashboard rides the roster; typed `LastChange` (`Eventing.Av`); generic action invocation; AV quick controls | ✅ done |
| R6 | `TryService`, trim/AOT declarations, README | ✅ done |
| — | `Announcements()` device activity timeline (library + dashboard per-card feed, expandable rows, counter) | ✅ done |
| — | `SearchAsync()` solicitation (+ dashboard Probe/'Solicit responses now'); memory audit + SoakTests (three accumulations fixed) | ✅ done |
| R7 | 4.1.0 release (branch, tag, Trusted Publishing + GitHub release) | ✅ released |
| 4.2 | Structural batch (on `dev/4.2.0`, 4.1.1 skipped): API ledger (PublicApiAnalyzers), `DescriptionCache` extraction, `IUpnpClient`, `EngineSource`/`TimedExchange`/`TestKit` dedups, DECISIONS.md, CI snupkg + trim smoke | ✅ released |
| 5.0 | Honest optionals: SSDP.UPnP.PCL 9.1 adoption, `BootSignature`, nullable `MaxAge`, `UpnpVersionClaims` (4 witnesses), clause 4.1.1 subscription cancellation + `ssdp:update`, `UPnP.Rx.Presence`, `LocalNetwork` + `[LoggerMessage]`, dashboard version badges / restart markers / retained departed devices / deep search; three-way review (deadlock, two cancellation bugs) | ✅ released |
| 6.0 P1 | Adopt SSDP.UPnP.PCL 10.0.0 + SimpleHttpListener.Rx 7.6.0 (on `dev/6.0.0`): `Received*` message split, `MulticastMSearch`, `MxSeconds`; `EntityType.Unknown` no longer kills discovery; M-SEARCH composition proved byte-identical across the upgrade | ✅ done |
| 6.0 P2 | Inherited analyzers verified live: SHLRX001/002 + SSDP001/003/005 seeded and fired in library, sample **and** test projects (package analyzers cross a ProjectReference edge); corpus clean at zero hits; CI tests the solution, not one project | ✅ done |
| 6.0 P3 | The API changes that delete rules: `mx` → `MxSeconds?` (floor becomes a type invariant, ceiling handed to SSDP001), `EventCallbackPort` → `ushort`, `UpnpClientOptions` range guards, IGD lease range enforced (`LeaseDurations`) so a negative lease stops meaning "permanent", nullable read-back on `PortMappingEntry`; `BannedSymbols` covers scheduler-less Rx time operators, CA2000 on in src+samples | ✅ done |
| 6.0 P4 | Analyzer infrastructure: `UPnP.Rx.Analyzers` + `.CodeFixes` (netstandard2.0, Roslyn 4.8 pinned in props) + `UPnP.Rx.Analyzers.Tests`; bundled into the library nupkg under `analyzers/dotnet/cs`; `OutputItemType="Analyzer"` references for every in-repo consumer; `PublishAot` condition verified; release tracking + infrastructure tests (severity, help anchors, declared-vs-implemented IDs) | ✅ done |
| 6.0 P5 | **UPNPRX001** - a port-mapping lease outside IGD's 0-604800 s, on the `lease` argument and on `LeaseDuration` initializers; two code fixes (clamp / explicitly indefinite, the latter only where a negative lease makes it a plausible reading); source stub + reflection guard against drift; mutation battery of 7, which found one inert branch and produced the test that closes it | ✅ done |
| 6.0 P6 | **UPNPRX002** - a `UpnpClientOptions` duration outside its documented range (non-positive timeouts, sub-second GENA timeout); provably-wrong values only, so a short-but-positive timeout stays silent. Fixing it exposed a real hole in the shared TimeSpan reader: .NET 8's integer factory overloads arrive with defaulted trailing arguments, which the reader had been rejecting | ✅ done |
| 6.0 P7 | **UPNPRX003** - the lease from a port-mapping call is discarded, so nothing ever removes the mapping; measured that CA2000 does *not* cover it (it tracks object creations, not a method's return value). Code fix rewrites to `await using`, and declines to rewrite an explicit `_ =`. Syntactic by design: anything the value flows into is somebody else's ownership | ✅ done |
| 6.0 P8 | **SCPD source generator** (`ScpdServiceGenerator`): a checked-in SCPD as an `AdditionalFile` plus `[ScpdService]` on a partial class emits typed action methods, nested result records, and the document's own range/allowed-value guards. Byte-identity adoption gate **green** for all five actions the document declares; `InternetGateway` does NOT adopt it, because the checked-in template is a subset. Cacheability + determinism + snapshot tests | ✅ done |
| 6.0 P9 | Docs + release verification: README rule/generator docs under explicit `<a id>` anchors (+ a test asserting every `helpLinkUri` resolves), analyzer and trim/AOT badges, version history; **packed-`.nupkg` consumer proof off a local feed** - all three rules plus SSDP001/003 and SHLRX001 (three package hops) fire for a real consumer, the generator runs, and the documented off-switch works; CI native-AOT publish **and run** on the runner's own RID; commented analyzer demos in the two console samples | ✅ done |
| 6.0 P10 | Code review (bug hunt / smells / dedup) + fixes: `GenaHeaders.ComposeTimeout` refuses a sub-second duration instead of composing `Second-0` or `Second--5`; the SCPD reader dedupes action names like it always did state variables; `UpnpApi` extracts the vocabulary three analyzers were each copying; UPNPRX002's diagnostic properties hoisted out of the report path. 11 mutations, all killing tests; both ledgers rolled to Shipped and both guards re-proved | ✅ done |

## Tree (current)

```
/
├── CLAUDE.md                  # session context: house rules, commands, workflow
├── CODEMAP.md                 # this file
├── README.md                  # package readme (ships in the nupkg)
├── assets/1iveowl-logo.png    # package icon
├── plan/upnp-rx-project-plan.md   # authoritative plan; §8 decisions, §9 upstream audit
├── Directory.Build.props      # net10.0, warnings-as-errors, package metadata, banned-API analyzer
├── BannedSymbols.txt          # wall-clock APIs banned (time model rule 1, error RS0030)
├── .editorconfig              # sibling-aligned house style + CA2007 (ConfigureAwait) as error in src/
├── global.json                # SDK 10.0.100, rollForward latestFeature
├── UPnP.Rx.slnx
├── .github/workflows/ci.yml   # build -warnaserror → test → pack; Trusted Publishing on v* tags
├── src/UPnP.Rx/               # the library (single package)
│   ├── UPnP.Rx.csproj         # deps: SSDP.UPnP.PCL 10, SimpleHttpListener.Rx 7.6, System.Reactive, Logging.Abstractions
│   ├── UpnpClient.cs          # edge: discovery over IControlPoint, lazy start (Defer),
│   │                          #   description cache (Location+ConfigId), M-SEARCH fan-out,
│   │                          #   DiscoverDescribedDevices (discovery+description, UDN-deduped)
│   ├── DiscoveredDevice.cs, DescribedDevice.cs, UpnpService.cs, IUpnpService.cs
│   ├── UpnpClientOptions.cs   # decision 6: search target/MX; TimeProvider (init-only); CPFN
│   ├── SearchTargets.cs       # RootDevice/All/DeviceType/ServiceType/Uuid over STType
│   ├── UpnpException.cs       # + UpnpActionException carrying UpnpError
│   ├── Scpd/                  # checked-in service descriptions the generator reads
│   │   └── WANIPConnection1.scpd.xml   # AdditionalFiles input; also packed at scpd/ for consumers
│   ├── Model/                 # UPnP.Rx.Model — immutable records
│   │   ├── ParseResult.cs     # copied from SSDP.UPnP.PCL (decision 5)
│   │   ├── DeviceDescription.cs   # DDD tree; Location + BaseUrl; SelfAndDescendants()
│   │   ├── ServiceDescription.cs, IconDescription.cs, SpecVersion.cs
│   │   ├── Scpd.cs, ActionDescription.cs, ArgumentDescription.cs, StateVariable.cs
│   │   ├── ScpdExtensions.cs  # ValidateAndOrderArguments — SCPD-driven marshalling
│   │   ├── ScpdServiceAttribute.cs     # marks a partial class for generation (public API,
│   │   │                               #   declared here rather than generated, by design)
│   │   └── ActionResult.cs, UpnpError.cs
│   ├── PortMapping/           # UPnP.Rx.PortMapping — the flagship (IGD client)
│   │   ├── PortMapper.cs      # DiscoverGatewayAsync (IGD:2+:1 merge) + one-liner AddPortMappingAsync
│   │   ├── InternetGateway.cs # WAN service priority (IPConn2/1, PPPConn2/1), typed actions,
│   │   │                      #   IAsyncEnumerable mapping enumeration (ends at gateway fault)
│   │   ├── PortMappingLease.cs    # auto-renew at half-life on TimeProvider; Events observable;
│   │   │                          #   DisposeAsync=delete, Dispose=abrupt (lease expires on router)
│   │   ├── PortMappingEntry.cs, Protocol.cs   # (Entry avoids namespace/type collision)
│   │   ├── WanIpConnection.cs # a partial class + [ScpdService]; every member is GENERATED
│   │   │                      #   from Scpd/WANIPConnection1.scpd.xml at build time
│   │   └── IInternetGateway.cs, IPortMappingLease.cs, ConnectionStatusInfo.cs
│   ├── Roster/                # UPnP.Rx.Roster (4.1): RosterChange union + RosterSource engine
│   │                          #   (presence, max-age expiry, replay, lazy self-heal)
│   ├── Announcement.cs        # Announcements(): kind-tagged parsed-envelope activity feed
│   ├── Eventing/Av/           # UPnP.Rx.Eventing.Av (4.1): LastChangeParser, AvPropertyChange,
│   │                          #   SelectAvChanges() — AV LastChange payload decoding
│   ├── Eventing/              # UPnP.Rx.Eventing — GENA (4.0): UpnpEvent union, GenaHeaders,
│   │                          #   GenaParser (pure), EventCallbackListener (NOTIFY routing,
│   │                          #   200/412), IGenaTransport + HttpGenaTransport,
│   │                          #   GenaSubscriptionSource (shared engine: replay, renewal,
│   │                          #   SEQ/gap recovery), EventingContext (client-wide wiring)
│   └── Parsing/               # UPnP.Rx.Parsing — pure, total, lenient
│       ├── DescriptionParser.cs   # DDD → DeviceDescription; URLBase honored; & repair
│       ├── ScpdParser.cs          # SCPD → Scpd (actions, state variables, ranges)
│       ├── SoapComposer.cs        # action envelope + SOAPACTION header (strict-out)
│       ├── SoapParser.cs          # response out-args + UPnPError fault parsing
│       └── XmlLeniency.cs         # internal: local-name/case-tolerant lookups, token cleanup
├── src/UPnP.Rx.Analyzers/     # THE ANALYZERS **AND** THE SOURCE GENERATOR (netstandard2.0,
│   │                          #   Roslyn 4.8; packed into UPnP.Rx.nupkg under analyzers/dotnet/cs -
│   │                          #   deliberately one assembly, not a separate generator project)
│   ├── DiagnosticIds.cs       # every ID + help-anchor helper; linked into the CodeFixes project
│   ├── UpnpApi.cs             # what the rules bind on (namespaces, method/parameter names) - one copy
│   ├── TimeSpanValue.cs       # reads a TimeSpan out of source; the whole false-positive budget
│   ├── LeaseDurationAnalyzer.cs      # UPNPRX001
│   ├── OptionRangeAnalyzer.cs        # UPNPRX002
│   ├── DiscardedLeaseAnalyzer.cs     # UPNPRX003
│   ├── ScpdServiceGenerator.cs       # ← the Roslyn source generator (IIncrementalGenerator)
│   ├── ScpdModel.cs                  #   its equatable pipeline model + the SCPD reader
│   ├── IsExternalInit.cs             # netstandard2.0 shim so records/init compile
│   └── AnalyzerReleases.{Shipped,Unshipped}.md   # RS2008 release tracking
├── src/UPnP.Rx.Analyzers.CodeFixes/  # fixes for UPNPRX001/003 (Workspaces layer; RS1038 forbids
│                                     #   an analyzer assembly referencing it, hence a 2nd project)
├── tests/UPnP.Rx.Analyzers.Tests/    # verifier-based rule tests, generator snapshots + cacheability,
│   └── Snapshots/                    #   and StubGuardTests holding the source stub to the real API
└── tests/UPnP.Rx.Tests/       # xUnit v3 + FakeTimeProvider
    ├── UPnP.Rx.Tests.csproj
    ├── DescriptionParserTests.cs, ScpdParserTests.cs, SoapTests.cs, ParseResultTests.cs
    ├── UpnpClientTests.cs     # discovery/dedup/cache/invoke/fault/lifecycle
    ├── PortMappingTests.cs    # gateway discovery/timeout, lease renewal on FakeTimeProvider,
    │                          #   failure→retry→Expired, dual disposal, AddAnyPortMapping
    ├── TestHelpers/           # FakeControlPoint (IControlPoint seam), FakeHttpHandler
    └── Fixtures/              # real captures (miniupnp testdesc: Linksys WAG200G w/ URLBase +
                               #   in-UDN line break, Orange Livebox IGD:2), WANIPConnection:1
                               #   SCPD (standardized template subset) + malformed variants
```

## Planned layout (lands per phase; namespaces from plan §5)

```
src/UPnP.Rx/
├── Model/            # UPnP.Rx.Model — immutable records                    (Phase 1)
│   ├── ParseResult.cs        # copied from SSDP.UPnP.PCL (decision 5)
│   ├── DeviceDescription.cs, ServiceDescription.cs, SpecVersion.cs, …
│   ├── Scpd.cs, ActionDescription.cs, ArgumentDescription.cs, StateVariable.cs   (Phase 2)
│   ├── ActionResult.cs, UpnpError.cs                                       (Phase 2)
│   └── UpnpClientOptions.cs, SearchTargets.cs                              (Phase 3)
├── Parsing/          # UPnP.Rx.Parsing — pure, total, no I/O/clock/logging
│   ├── DescriptionParser.cs   # DDD → DeviceDescription                    (Phase 1)
│   ├── ScpdParser.cs                                                       (Phase 2)
│   ├── SoapComposer.cs, SoapParser.cs                                      (Phase 2)
│   └── XmlLeniency.cs         # namespace-tolerant XDocument helpers       (Phase 1)
├── UpnpClient.cs     # UPnP.Rx — edge: discovery wiring over IControlPoint (Phase 3)
├── DiscoveredDevice.cs, DescribedDevice.cs, UpnpService.cs                 (Phase 3)
├── UpnpException.cs, UpnpActionException.cs                                (Phase 3)
└── PortMapping/      # UPnP.Rx.PortMapping — the flagship                  (Phase 4)
    ├── PortMapper.cs, InternetGateway.cs
    ├── PortMappingLease.cs    # auto-renew loop; IAsyncDisposable + IDisposable (decision 3)
    └── PortMapping.cs, PortMappingEvent.cs, Protocol.cs

tests/UPnP.Rx.Tests/
├── Fixtures/         # real-device DDD/SCPD XML, incl. malformed captures  (Phase 1+)
├── *ParserTests.cs   # pure: input → record assertions                     (Phase 1+)
├── UpnpClientTests.cs        # Subject-driven IControlPoint fake           (Phase 3)
└── PortMappingLeaseTests.cs  # FakeTimeProvider-driven renewal             (Phase 4)

samples/
├── Sample.PortMapper/         # ✅ discover gateway, external IP, list mappings, --map demo
├── Sample.Browser/            # ✅ discover everything, dump device trees + services
├── Sample.Eventing/           # ✅ subscribe to a service, print UpnpEvents (author test guide)
├── Sample.Dashboard/          # ✅ Blazor host: SSDP listening + SignalR hub with roster replay
│                              #    (design + backlog: plan/sample-dashboard-plan.md)
└── Sample.Dashboard.Client/   # ✅ WASM: SignalR → DynamicData → ReactiveUI → FluentUI cards
```

## Key seams (for tests and consumers)

- **`IControlPoint`** (from SSDP.UPnP.PCL) — `UpnpClient`'s bring-your-own constructor; tests drive it with a Subject via `HotStart`. Never touch multicast in tests.
- **`HttpClient`** — injectable everywhere descriptions are fetched / SOAP is posted; tests use a fake `HttpMessageHandler`.
- **`TimeProvider`** — carried in `UpnpClientOptions` (init-only; deliberate divergence from upstream's settable property, plan §9). Tests inject `FakeTimeProvider` — one clock per test.
