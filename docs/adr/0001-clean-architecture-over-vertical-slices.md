# ADR 0001 — Clean/Onion layering instead of Vertical Slice Architecture

- **Status:** Accepted
- **Date:** 2026-06-24
- **Deciders:** PoLocalCompare maintainers

## Context

The global engineering standard for `Po*` solutions mandates **Vertical Slice Architecture (VSA)**:
collapse technical layers and group endpoint + handler + DTO per feature in a flat folder so that a
single feature is one unit of context.

PoLocalCompare instead ships a **Clean/Onion** layout with four projects:

```
PoLocalCompare.Domain         ← entities, value objects, domain services (zero deps)
PoLocalCompare.Application     ← use-case handlers + repository/proxy interfaces (depends on Domain)
PoLocalCompare.Infrastructure  ← Table Storage repos, Foundry/Ollama proxies, Key Vault (implements Application)
PoLocalCompare.Api             ← minimal-API endpoints, SignalR hubs, composition root
```

This is a real, standing contradiction with the global mandate. Two recurring questions follow from it:
"is this a mistake to fix?" and "where does new code go?". This ADR settles both.

## Decision

**Keep the Clean/Onion layering. Do not refactor to VSA.**

The driver for VSA is context locality for humans and agents. This solution already gets most of that
benefit a different way: the Application layer is organised **by feature** (`Duels/CommenceDuel`,
`Leaderboard/GetLeaderboard`, `Models/RegisterModel`), and each use case is a self-contained
command/query + handler pair. The technical seams that VSA removes are load-bearing here:

- The **Domain** holds non-trivial, independently-tested logic (`EloCalculator`, `GreenStatsCalculator`,
  `HtmlOutputQualityScorer`). Keeping it dependency-free is what makes `PoLocalCompare.Unit.Tests` fast
  and pure.
- The **Infrastructure** boundary lets local dev swap real Azure (Table Storage, Foundry) for Azurite and
  mock proxies behind `Application` interfaces — the same seam the integration tests drive via Testcontainers.

Rewriting four working, tested projects into flat slices would be high-churn, high-risk, and would delete
the very test seams that protect the Elo scoring logic. That is a poor trade against the zero-waste rule.

## Consequences

- **New features follow the existing grain:** add a `Feature/UseCase` folder under `Application` with the
  command/query + handler, an interface in `Application/Interfaces` if it needs I/O, the implementation in
  `Infrastructure`, and an endpoint in `Api/Endpoints` registered via `MapGroup`.
- **The global VSA mandate is explicitly waived for this repo**, recorded here so future agents do not
  "helpfully" start a migration. `AGENT.MD §3` points to this ADR.
- If the Application layer ever grows handlers with heavy cross-feature coupling, revisit this decision —
  that is the signal VSA was meant to address.
