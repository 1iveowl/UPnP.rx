# CODEMAP.md — UPnP.Rx

Repo map + phase status. **Update this file in every phase commit** (status table + tree). The full phase definitions live in [plan §7](plan/upnp-rx-project-plan.md).

## Phase status

| Phase | Deliverable (short) | Status |
|---|---|---|
| 0 | Repo infrastructure (props, editorconfig, slnx, CI skeleton, test project) | ✅ done |
| 1 | Model records, `ParseResult<T>`, `DescriptionParser` + fixtures | ✅ done |
| 2 | `ScpdParser`, `SoapComposer`/`SoapParser` | ✅ done |
| 3 | Edge: `UpnpClient` / `DiscoveredDevice` / `DescribedDevice` / `UpnpService` | ✅ done |
| 4 | `UPnP.Rx.PortMapping` + `PortMappingLease` + `Sample.PortMapper` | — |
| 5 | UDA 2.0 clause 2/3 compliance review + fixes | — |
| 6 | Packaging, README, samples polish, CI publish job | — |
| 7 | v1.0.0 release (branch, tag, Trusted Publishing) | — |

## Tree (current)

```
/
├── CLAUDE.md                  # session context: house rules, commands, workflow
├── CODEMAP.md                 # this file
├── plan/upnp-rx-project-plan.md   # authoritative plan; §8 decisions, §9 upstream audit
├── Directory.Build.props      # net10.0, warnings-as-errors, package metadata, banned-API analyzer
├── BannedSymbols.txt          # wall-clock APIs banned (time model rule 1, error RS0030)
├── .editorconfig              # sibling-aligned house style + CA2007 (ConfigureAwait) as error in src/
├── global.json                # SDK 10.0.100, rollForward latestFeature
├── UPnP.Rx.slnx
├── .github/workflows/ci.yml   # restore → build -warnaserror → test → pack (publish job: Phase 6)
├── src/UPnP.Rx/               # the library (single package)
│   ├── UPnP.Rx.csproj         # deps: SSDP.UPnP.PCL, System.Reactive, Logging.Abstractions
│   ├── UpnpClient.cs          # edge: discovery over IControlPoint, lazy start (Defer),
│   │                          #   description cache (Location+ConfigId), M-SEARCH fan-out
│   ├── DiscoveredDevice.cs, DescribedDevice.cs, UpnpService.cs
│   ├── UpnpClientOptions.cs   # decision 6: search target/MX; TimeProvider (init-only); CPFN
│   ├── SearchTargets.cs       # RootDevice/All/DeviceType/ServiceType/Uuid over STType
│   ├── UpnpException.cs       # + UpnpActionException carrying UpnpError
│   ├── Model/                 # UPnP.Rx.Model — immutable records
│   │   ├── ParseResult.cs     # copied from SSDP.UPnP.PCL (decision 5)
│   │   ├── DeviceDescription.cs   # DDD tree; Location + BaseUrl; SelfAndDescendants()
│   │   ├── ServiceDescription.cs, IconDescription.cs, SpecVersion.cs
│   │   ├── Scpd.cs, ActionDescription.cs, ArgumentDescription.cs, StateVariable.cs
│   │   └── ActionResult.cs, UpnpError.cs
│   └── Parsing/               # UPnP.Rx.Parsing — pure, total, lenient
│       ├── DescriptionParser.cs   # DDD → DeviceDescription; URLBase honored; & repair
│       ├── ScpdParser.cs          # SCPD → Scpd (actions, state variables, ranges)
│       ├── SoapComposer.cs        # action envelope + SOAPACTION header (strict-out)
│       ├── SoapParser.cs          # response out-args + UPnPError fault parsing
│       └── XmlLeniency.cs         # internal: local-name/case-tolerant lookups, token cleanup
└── tests/UPnP.Rx.Tests/       # xUnit v3 + FakeTimeProvider
    ├── UPnP.Rx.Tests.csproj
    ├── DescriptionParserTests.cs, ScpdParserTests.cs, SoapTests.cs, ParseResultTests.cs
    ├── UpnpClientTests.cs     # discovery/dedup/cache/invoke/fault/lifecycle
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
├── Sample.PortMapper/                                                      (Phase 4)
└── Sample.Browser/                                                         (Phase 6)
```

## Key seams (for tests and consumers)

- **`IControlPoint`** (from SSDP.UPnP.PCL) — `UpnpClient`'s bring-your-own constructor; tests drive it with a Subject via `HotStart`. Never touch multicast in tests.
- **`HttpClient`** — injectable everywhere descriptions are fetched / SOAP is posted; tests use a fake `HttpMessageHandler`.
- **`TimeProvider`** — carried in `UpnpClientOptions` (init-only; deliberate divergence from upstream's settable property, plan §9). Tests inject `FakeTimeProvider` — one clock per test.
