# ADR 0002 — Migrate to Vertical Slice Architecture

- **Status:** Accepted (supersedes [ADR 0001](0001-clean-architecture-over-vertical-slices.md))
- **Date:** 2026-07-06
- **Deciders:** PoLocalCompare maintainers

## Context

ADR 0001 kept Clean/Onion layering as a deliberate deviation from the global `Po*` standard (§2.1),
which mandates Vertical Slice Architecture. The maintainers have since directed full reconciliation
with the standard.

## Decision

Collapse `PoLocalCompare.Domain`, `PoLocalCompare.Application`, and `PoLocalCompare.Infrastructure`
into `PoLocalCompare.Api`, organised as feature slices:

```
src/PoLocalCompare.Api/
  Features/
    Duels/         endpoints, commence/get/list/record-verdict handlers, Duel/DuelResult entities,
                   Duel & DuelResult repositories, DuelExecutionService, AutoJudgeService, DuelHub
    Leaderboard/   endpoints, leaderboard/kill-list handlers, EloRecord, EloCalculator, EloHistoryRepository
    Models/        endpoints, list/register handlers, Model entity, ModelRepository, ModelSeeder
    Archive/       lab-report export handler + HTML renderer
    Ollama/        status/pull endpoints
    Lobby/         LobbyHub
    Diagnostics/   /health, /api/diag/*, E2E helpers
  Common/
    Domain/        cross-slice calculators & value objects (green score, HTML quality)
    Inference/     IRemoteInferenceProxy + Foundry/Ollama typed-HttpClient proxies
    Background/    background task queue + hosted service
    KeyVault/      prefix-scoped Key Vault configuration
    Persistence/   Azurite table bootstrap
    Telemetry/     RateLimitedSampler (§6.3)
```

`GlobalUsings.cs` exposes all slice namespaces project-wide, so cross-slice references need no
per-file usings. Only `PoLocalCompare.Api`, `PoLocalCompare.Shared`, and `PoLocalCompare.Client`
remain as src projects.

## Consequences

- One project to navigate; a feature is one folder.
- Unit tests reference `PoLocalCompare.Api` directly; the pure calculators remain isolated classes
  and stay trivially unit-testable.
- The mock/emulator seam ADR 0001 defended survives as the repository and proxy *interfaces*,
  which now live beside their single implementations in each slice.
- Test projects were renamed/flattened per §2.2: `tests/PoLocalCompare.{UnitTests,IntegrationTests,E2EAPI,E2EUI}`.
