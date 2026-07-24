# Agent instructions — UPnP.Rx

The instructions for AI coding agents working in this repository live in
[CLAUDE.md](CLAUDE.md) — house rules (time model, disposal model, Rx/functional
rules), commands, constraints and workflow. Read it first, then:

- [plan/upnp-rx-project-plan.md](plan/upnp-rx-project-plan.md) — the authoritative plan: settled policies (§5), resolved decisions (§8 — do not relitigate), upstream verification notes (§9).
- [CODEMAP.md](CODEMAP.md) — repo structure and phase status; update it with every phase commit.

One rule above all: build + tests green at every commit (`dotnet build UPnP.Rx.slnx -c Release` / `dotnet test UPnP.Rx.slnx -c Release`), and never modify the upstream libraries.
