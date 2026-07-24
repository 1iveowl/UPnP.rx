# CLAUDE.md — UPnP.Rx

Rx-based UPnP control point for .NET 10: discover → describe → control (v1), eventing (v2), with an IGD port-mapping client as the flagship. Sibling of `SSDP.UPnP.PCL` / `SimpleHttpListener.Rx` (same author: `1iveowl`), AI-assisted from day one.

## Read these first

- [plan/upnp-rx-project-plan.md](plan/upnp-rx-project-plan.md) — the authoritative plan: vision, scope, architecture, **settled policies** (time model, disposal model, Rx/functional rules), phased build plan (§7), resolved decisions (§8 — do not relitigate), upstream verification notes (§9).
- [CODEMAP.md](CODEMAP.md) — repo structure, current phase status, planned layout. **Update it in every phase commit.**

## Commands

```bash
dotnet build UPnP.Rx.slnx -c Release          # TreatWarningsAsErrors everywhere
dotnet test UPnP.Rx.slnx -c Release
dotnet pack src/UPnP.Rx/UPnP.Rx.csproj -c Release
```

## Non-negotiable house rules (details in the plan — read the section before writing that kind of code)

1. **Time model** (plan §5): `TimeProvider` is the one clock. Wall-clock APIs are banned — build-enforced via `BannedSymbols.txt` (error RS0030). Time-based Rx operators must take an explicit scheduler wrapping the component's `TimeProvider`. `Microsoft.Reactive.Testing`'s `TestScheduler` is banned; tests use one `FakeTimeProvider`.
2. **Disposal model** (plan §5): `DisposeAsync` = graceful protocol goodbye; sync `Dispose` = abrupt release only, never fire-and-forget async. `FinallyAsync` pattern banned; per-subscription async teardown uses `Observable.Create`'s async-subscribe overload.
3. **Rx/functional rules** (plan §5, 10-point checklist): never `Subscribe(async …)`; no blocking (`.Wait()`/`.Result`); document hot/cold on every public observable; no Subjects in public API; per-item failure is data, `OnError` is source death; parsing is pure/total returning `ParseResult<T>`; `ConfigureAwait(false)` on every await in `src/` (CA2007 = error; off in tests/samples).
4. **Leniency**: strict in what we send, lenient in what we accept. Parsers only fail when a document identifies nothing; unparsable optional fields stay unset.
5. **XML docs on every public member** (build-enforced). Docs ship in the nupkg.

## Constraints

- **Never modify the upstream libraries** (`SSDP.UPnP.PCL`, `SimpleHttpListener.Rx`, `HttpMachine.PCL`). Gaps become "upstream issue candidates" in plan §9.
- **Multicast does not work in devcontainers** — tests never depend on it; SSDP is faked at the `IControlPoint` seam (Subject-driven `HotStart`, see sibling repo's `ControlPointTests`). Real-hardware smoke tests are the author's manual pre-tag step.
- Check nuget.org for current package versions when adding references — don't trust remembered versions.
- Dependencies (v1) are locked by plan §5: `SSDP.UPnP.PCL`, `System.Reactive`, `Microsoft.Extensions.Logging.Abstractions` — nothing else without author sign-off.

## Workflow

- Build phase by phase per plan §7; **one commit per phase**, build + tests green at every commit. Update CODEMAP.md's status table in the same commit.
- Test-first bias: pure parsers get real-device XML fixtures (including malformed ones); edge classes get fake `HttpMessageHandler`, `FakeTimeProvider`, and Subject-driven `IControlPoint` fakes.
- Release discipline (plan §6): `releases/x.y.z` branches frozen at tag; any post-tag change — even docs — is a new patch version. Publishing is via NuGet Trusted Publishing on `v*` tags.
- The author (Jasper, `1iveowl`) reviews spec-compliance findings and open design questions; record resolved decisions in plan §8.
